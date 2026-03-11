using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GCAllocBreakdown.Editor
{
    internal static class SnapshotSerializer
    {
        const int k_GCAllocSnapshot = 'G' | ('C' << 8) | ('A' << 16) | ('S' << 24);
        const int k_Version = 3;
        const int k_StringsPerAlloc = 11;
        const int k_BytesPerAlloc = 92; // 48 numeric + 44 string indices
        const int k_ChunkAllocs = 8192;
        const int k_IOBuffer = 1 << 22; // 4 MB

        // ═══════════════════════════════════════════════════
        //  WRITE (skeleton + heavy data)
        // ═══════════════════════════════════════════════════

        public static void Write(string path, AnalysisSnapshot snapshot, GraphFrameStore store)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, k_IOBuffer);
            using var w = new BinaryWriter(fs, System.Text.Encoding.UTF8);

            w.Write(k_GCAllocSnapshot);
            w.Write(k_Version);

            // ── Skeleton section ──

            // Metadata
            w.Write(snapshot.TotalBytes);
            w.Write(snapshot.TotalCount);
            w.Write(snapshot.FrameStart);
            w.Write(snapshot.FrameEnd);
            w.Write(snapshot.HadCallStacks);

            // SortedThreadNames
            w.Write(snapshot.SortedThreadNames.Count);
            for (int i = 0; i < snapshot.SortedThreadNames.Count; i++)
                w.Write(snapshot.SortedThreadNames[i] ?? "");

            // GraphFrameStore — bulk copy long[] via Buffer.BlockCopy
            int storeStart = store?.FullFrameStart ?? 0;
            int storeEnd = store?.FullFrameEnd ?? 0;
            long[] storeBytes = store?.FullFrameBytes;
            w.Write(storeStart);
            w.Write(storeEnd);
            int fbLen = storeBytes?.Length ?? 0;
            w.Write(fbLen);
            if (fbLen > 0)
            {
                var fbBuf = new byte[fbLen * 8];
                Buffer.BlockCopy(storeBytes, 0, fbBuf, 0, fbBuf.Length);
                w.Write(fbBuf);
            }

            // Groups (both groupings)
            WriteGroupList(w, snapshot.GroupsByFullCallstack);
            WriteGroupList(w, snapshot.GroupsByTopFrame);

            // Alloc data offset placeholder — record position, write 0, fill in later
            long offsetPos = fs.Position;
            w.Write((long)0);

            // ── Heavy data section ──

            long allocDataStart = fs.Position;
            var allocs = snapshot.RawAllocations;

            // Pass 1: Build string table and callstack table (no per-alloc index array).
            // The old approach allocated int[allocCount * 11] (~328 MB for 1.87M allocs).
            // Instead we build the dictionary here and look up indices during the write pass.
            var stringToIdx = new Dictionary<string, int>(4096);
            var strings = new List<string>(4096);

            // Track which FullCallstackIds we've already interned to skip
            // redundant callstack frame interning (stacks are shared across millions of allocs)
            int maxCsId = 0;
            var csInterned = new HashSet<int>();

            for (int i = 0; i < allocs.Count; i++)
            {
                var a = allocs[i];
                Intern(a.ThreadDisplayName, stringToIdx, strings);
                Intern(a.ThreadName, stringToIdx, strings);
                Intern(a.ThreadGroupName, stringToIdx, strings);
                Intern(a.ParentMethod, stringToIdx, strings);
                Intern(a.HierarchyPath, stringToIdx, strings);
                Intern(a.FullCallstackKey, stringToIdx, strings);
                Intern(a.TopFrameKey, stringToIdx, strings);
                Intern(a.DisplayName, stringToIdx, strings);
                Intern(a.DisplayNameWithAssembly, stringToIdx, strings);
                Intern(a.FormattedBytes, stringToIdx, strings);
                Intern(a.FormattedFrame, stringToIdx, strings);

                int csId = a.FullCallstackId;
                if (csId >= maxCsId) maxCsId = csId + 1;

                if (a.ResolvedCallStack != null && csId >= 0 && csInterned.Add(csId))
                {
                    for (int f = 0; f < a.ResolvedCallStack.Count; f++)
                    {
                        Intern(a.ResolvedCallStack[f].RawMethodName, stringToIdx, strings);
                        Intern(a.ResolvedCallStack[f].SourceFile, stringToIdx, strings);
                    }
                }
            }

            // Write string table
            w.Write(strings.Count);
            for (int i = 0; i < strings.Count; i++)
                w.Write(strings[i]);

            // Build callstack table indexed by FullCallstackId
            var callstacks = new List<ResolvedFrame>[maxCsId];
            for (int i = 0; i < allocs.Count; i++)
            {
                var a = allocs[i];
                if (a.FullCallstackId >= 0 && callstacks[a.FullCallstackId] == null)
                    callstacks[a.FullCallstackId] = a.ResolvedCallStack;
            }

            // Write callstack table
            w.Write(maxCsId);
            for (int i = 0; i < maxCsId; i++)
            {
                var cs = callstacks[i];
                if (cs == null || cs.Count == 0)
                {
                    w.Write(-1);
                    continue;
                }
                w.Write(cs.Count);
                for (int f = 0; f < cs.Count; f++)
                {
                    w.Write(stringToIdx[cs[f].RawMethodName ?? ""]);
                    w.Write(stringToIdx[cs[f].SourceFile ?? ""]);
                    w.Write(cs[f].SourceLine);
                }
            }

            // Pass 2: Allocations — chunked bulk write with inline string index lookups
            long checksum = 0;
            w.Write(allocs.Count);
            var chunkBuf = new byte[k_ChunkAllocs * k_BytesPerAlloc];

            for (int start = 0; start < allocs.Count; start += k_ChunkAllocs)
            {
                int end = Math.Min(start + k_ChunkAllocs, allocs.Count);
                for (int i = start; i < end; i++)
                {
                    var a = allocs[i];
                    checksum += a.Bytes;
                    int off = (i - start) * k_BytesPerAlloc;

                    PackInt64(chunkBuf, off, a.Bytes); off += 8;
                    PackInt32(chunkBuf, off, a.FrameIndex); off += 4;
                    PackInt32(chunkBuf, off, a.RawSampleIndex); off += 4;
                    PackUInt64(chunkBuf, off, a.ThreadId); off += 8;
                    PackInt32(chunkBuf, off, a.ThreadIndex); off += 4;
                    PackInt32(chunkBuf, off, a.FullCallstackId); off += 4;
                    PackInt32(chunkBuf, off, a.TopFrameId); off += 4;
                    PackInt32(chunkBuf, off, a.FullCallstackGroupIndex); off += 4;
                    PackInt32(chunkBuf, off, a.TopFrameGroupIndex); off += 4;
                    PackInt32(chunkBuf, off, a.ThreadAllocCountIndex); off += 4;

                    PackInt32(chunkBuf, off, stringToIdx[a.ThreadDisplayName ?? ""]); off += 4;
                    PackInt32(chunkBuf, off, stringToIdx[a.ThreadName ?? ""]); off += 4;
                    PackInt32(chunkBuf, off, stringToIdx[a.ThreadGroupName ?? ""]); off += 4;
                    PackInt32(chunkBuf, off, stringToIdx[a.ParentMethod ?? ""]); off += 4;
                    PackInt32(chunkBuf, off, stringToIdx[a.HierarchyPath ?? ""]); off += 4;
                    PackInt32(chunkBuf, off, stringToIdx[a.FullCallstackKey ?? ""]); off += 4;
                    PackInt32(chunkBuf, off, stringToIdx[a.TopFrameKey ?? ""]); off += 4;
                    PackInt32(chunkBuf, off, stringToIdx[a.DisplayName ?? ""]); off += 4;
                    PackInt32(chunkBuf, off, stringToIdx[a.DisplayNameWithAssembly ?? ""]); off += 4;
                    PackInt32(chunkBuf, off, stringToIdx[a.FormattedBytes ?? ""]); off += 4;
                    PackInt32(chunkBuf, off, stringToIdx[a.FormattedFrame ?? ""]);
                }
                w.Write(chunkBuf, 0, (end - start) * k_BytesPerAlloc);
            }

            w.Write(checksum);

            // Patch the alloc data offset into the skeleton section
            w.Flush();
            fs.Seek(offsetPos, SeekOrigin.Begin);
            w.Write(allocDataStart);
        }

        // ═══════════════════════════════════════════════════
        //  READ SKELETON (fast — groups + frame bytes, no allocations)
        // ═══════════════════════════════════════════════════

        public static AnalysisSnapshot ReadSkeleton(string path, out GraphFrameStore store, out long allocDataOffset)
        {
            store = new GraphFrameStore();
            allocDataOffset = 0;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, k_IOBuffer);
            using var r = new BinaryReader(fs, System.Text.Encoding.UTF8);

            ReadAndValidateHeader(r);

            var snapshot = new AnalysisSnapshot();

            // Metadata
            snapshot.TotalBytes = r.ReadInt64();
            snapshot.TotalCount = r.ReadInt32();
            snapshot.FrameStart = r.ReadInt32();
            snapshot.FrameEnd = r.ReadInt32();
            snapshot.HadCallStacks = r.ReadBoolean();

            // SortedThreadNames
            int threadCount = r.ReadInt32();
            snapshot.SortedThreadNames = new List<string>(threadCount);
            for (int i = 0; i < threadCount; i++)
                snapshot.SortedThreadNames.Add(r.ReadString());

            // GraphFrameStore — bulk copy via Buffer.BlockCopy
            store.FullFrameStart = r.ReadInt32();
            store.FullFrameEnd = r.ReadInt32();
            int fbLen = r.ReadInt32();
            if (fbLen > 0)
            {
                var fbBuf = r.ReadBytes(fbLen * 8);
                store.FullFrameBytes = new long[fbLen];
                Buffer.BlockCopy(fbBuf, 0, store.FullFrameBytes, 0, fbBuf.Length);
            }

            // Groups
            snapshot.EnsureNonSerializedLists();
            ReadGroupList(r, snapshot.GroupsByFullCallstack);
            ReadGroupList(r, snapshot.GroupsByTopFrame);

            // Alloc data offset
            allocDataOffset = r.ReadInt64();

            return snapshot;
        }

        // ═══════════════════════════════════════════════════
        //  READ ALLOCATIONS (heavy — called on background thread)
        // ═══════════════════════════════════════════════════

        public static List<RawAllocation> ReadAllocations(string path, long allocDataOffset)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, k_IOBuffer);
            using var r = new BinaryReader(fs, System.Text.Encoding.UTF8);

            fs.Seek(allocDataOffset, SeekOrigin.Begin);

            // Read string table
            int stringCount = r.ReadInt32();
            var strings = new string[stringCount];
            for (int i = 0; i < stringCount; i++)
                strings[i] = r.ReadString();

            // Read callstack table (indexed by FullCallstackId)
            int csCount = r.ReadInt32();
            var callstacks = new List<ResolvedFrame>[csCount];
            for (int i = 0; i < csCount; i++)
            {
                int frameCount = r.ReadInt32();
                if (frameCount < 0) continue; // -1 = empty slot
                var frames = new List<ResolvedFrame>(frameCount);
                for (int f = 0; f < frameCount; f++)
                {
                    frames.Add(new ResolvedFrame
                    {
                        RawMethodName = strings[r.ReadInt32()],
                        SourceFile = strings[r.ReadInt32()],
                        SourceLine = r.ReadInt32()
                    });
                }
                callstacks[i] = frames;
            }

            // Allocations — chunked bulk read
            int allocCount = r.ReadInt32();
            var allocs = new List<RawAllocation>(allocCount);
            long checksum = 0;
            var chunkBuf = new byte[k_ChunkAllocs * k_BytesPerAlloc];

            for (int start = 0; start < allocCount; start += k_ChunkAllocs)
            {
                int end = Math.Min(start + k_ChunkAllocs, allocCount);
                int bytesNeeded = (end - start) * k_BytesPerAlloc;

                int totalRead = 0;
                while (totalRead < bytesNeeded)
                {
                    int n = r.Read(chunkBuf, totalRead, bytesNeeded - totalRead);
                    if (n == 0) throw new EndOfStreamException("Unexpected end of snapshot file");
                    totalRead += n;
                }

                for (int i = 0; i < end - start; i++)
                {
                    int off = i * k_BytesPerAlloc;
                    var a = new RawAllocation();

                    a.Bytes = UnpackInt64(chunkBuf, off); off += 8;
                    checksum += a.Bytes;
                    a.FrameIndex = UnpackInt32(chunkBuf, off); off += 4;
                    a.RawSampleIndex = UnpackInt32(chunkBuf, off); off += 4;
                    a.ThreadId = UnpackUInt64(chunkBuf, off); off += 8;
                    a.ThreadIndex = UnpackInt32(chunkBuf, off); off += 4;
                    a.FullCallstackId = UnpackInt32(chunkBuf, off); off += 4;
                    a.TopFrameId = UnpackInt32(chunkBuf, off); off += 4;
                    a.FullCallstackGroupIndex = UnpackInt32(chunkBuf, off); off += 4;
                    a.TopFrameGroupIndex = UnpackInt32(chunkBuf, off); off += 4;
                    a.ThreadAllocCountIndex = UnpackInt32(chunkBuf, off); off += 4;

                    a.ThreadDisplayName = strings[UnpackInt32(chunkBuf, off)]; off += 4;
                    a.ThreadName = strings[UnpackInt32(chunkBuf, off)]; off += 4;
                    a.ThreadGroupName = strings[UnpackInt32(chunkBuf, off)]; off += 4;
                    a.ParentMethod = strings[UnpackInt32(chunkBuf, off)]; off += 4;
                    a.HierarchyPath = strings[UnpackInt32(chunkBuf, off)]; off += 4;
                    a.FullCallstackKey = strings[UnpackInt32(chunkBuf, off)]; off += 4;
                    a.TopFrameKey = strings[UnpackInt32(chunkBuf, off)]; off += 4;
                    a.DisplayName = strings[UnpackInt32(chunkBuf, off)]; off += 4;
                    a.DisplayNameWithAssembly = strings[UnpackInt32(chunkBuf, off)]; off += 4;
                    a.FormattedBytes = strings[UnpackInt32(chunkBuf, off)]; off += 4;
                    a.FormattedFrame = strings[UnpackInt32(chunkBuf, off)];

                    a.ResolvedCallStack = (a.FullCallstackId >= 0 && a.FullCallstackId < csCount && callstacks[a.FullCallstackId] != null)
                        ? callstacks[a.FullCallstackId]
                        : new List<ResolvedFrame>(0);

                    allocs.Add(a);
                }
            }

            long expectedChecksum = r.ReadInt64();
            if (checksum != expectedChecksum)
                Debug.LogError($"[SnapshotSerializer] Checksum mismatch: expected {expectedChecksum}, got {checksum}. Data may be corrupt.");

            return allocs;
        }

        // ═══════════════════════════════════════════════════
        //  READ (full synchronous — skeleton + allocations in one call)
        // ═══════════════════════════════════════════════════

        public static AnalysisSnapshot Read(string path, out GraphFrameStore store)
        {
            var snapshot = ReadSkeleton(path, out store, out long offset);
            snapshot.RawAllocations = ReadAllocations(path, offset);
            snapshot.EnsureNonSerializedLists();
            return snapshot;
        }

        // ═══════════════════════════════════════════════════
        //  GROUP SERIALIZATION
        // ═══════════════════════════════════════════════════

        static void WriteGroupList(BinaryWriter w, List<CallsiteGroup> groups)
        {
            int count = groups?.Count ?? 0;
            w.Write(count);
            for (int i = 0; i < count; i++)
                WriteGroup(w, groups[i]);
        }

        static void WriteGroup(BinaryWriter w, CallsiteGroup g)
        {
            w.Write(g.Key ?? "");
            w.Write(g.DisplayName ?? "");
            w.Write(g.DisplayNameNoAssembly ?? "");
            w.Write(g.DisplayNameWithAssembly ?? "");
            w.Write(g.HierarchyPath ?? "");
            w.Write(g.GroupIndex);
            w.Write(g.TotalBytes);
            w.Write(g.Count);
            w.Write(g.Percentage);

            // FirstAlloc fields for CPU module selection
            w.Write(g.FirstAllocFrameIndex);
            w.Write(g.FirstAllocRawSampleIndex);
            w.Write(g.FirstAllocThreadName ?? "");
            w.Write(g.FirstAllocThreadGroupName ?? "");
            w.Write(g.FirstAllocThreadId);

            // ResolvedCallStack
            int csCount = g.ResolvedCallStack?.Count ?? 0;
            w.Write(csCount);
            for (int f = 0; f < csCount; f++)
            {
                w.Write(g.ResolvedCallStack[f].RawMethodName ?? "");
                w.Write(g.ResolvedCallStack[f].SourceFile ?? "");
                w.Write(g.ResolvedCallStack[f].SourceLine);
            }

            // Per-frame stats
            w.Write(g.MeanBytesPerFrame);
            w.Write(g.MedianBytesPerFrame);
            w.Write(g.MinBytesPerFrame);
            w.Write(g.MaxBytesPerFrame);
            w.Write(g.MinFrame);
            w.Write(g.MaxFrame);
            w.Write(g.FirstFrame);

            // TopWorst arrays
            int twCount = g.TopWorstFrameIndices?.Length ?? 0;
            w.Write(twCount);
            for (int t = 0; t < twCount; t++)
            {
                w.Write(g.TopWorstFrameIndices[t]);
                w.Write(g.TopWorstFrameBytes[t]);
            }

            // Pre-computed display strings
            w.Write(g.FormattedBytes ?? "");
            w.Write(g.FormattedCount ?? "");
            w.Write(g.FormattedAvg ?? "");
            w.Write(g.FormattedPct ?? "");
            w.Write(g.RangeBytesPerFrame);
            w.Write(g.FormattedMedian ?? "");
            w.Write(g.FormattedMin ?? "");
            w.Write(g.FormattedMax ?? "");
            w.Write(g.FormattedMean ?? "");
            w.Write(g.FormattedRange ?? "");
            w.Write(g.FormattedFirst ?? "");

            int ftwCount = g.FormattedTopWorst?.Length ?? 0;
            w.Write(ftwCount);
            for (int t = 0; t < ftwCount; t++)
                w.Write(g.FormattedTopWorst[t] ?? "");
        }

        static void ReadGroupList(BinaryReader r, List<CallsiteGroup> target)
        {
            int count = r.ReadInt32();
            target.Clear();
            if (target.Capacity < count)
                target.Capacity = count;
            for (int i = 0; i < count; i++)
                target.Add(ReadGroup(r));
        }

        static CallsiteGroup ReadGroup(BinaryReader r)
        {
            var g = new CallsiteGroup
            {
                Key = r.ReadString(),
                DisplayName = r.ReadString(),
                DisplayNameNoAssembly = r.ReadString(),
                DisplayNameWithAssembly = r.ReadString(),
                HierarchyPath = r.ReadString(),
                GroupIndex = r.ReadInt32(),
                TotalBytes = r.ReadInt64(),
                Count = r.ReadInt32(),
                Percentage = r.ReadSingle()
            };

            // FirstAlloc fields for CPU module selection
            g.FirstAllocFrameIndex = r.ReadInt32();
            g.FirstAllocRawSampleIndex = r.ReadInt32();
            g.FirstAllocThreadName = r.ReadString();
            g.FirstAllocThreadGroupName = r.ReadString();
            g.FirstAllocThreadId = r.ReadUInt64();

            // ResolvedCallStack
            int csCount = r.ReadInt32();
            if (csCount > 0)
            {
                g.ResolvedCallStack = new List<ResolvedFrame>(csCount);
                for (int f = 0; f < csCount; f++)
                {
                    g.ResolvedCallStack.Add(new ResolvedFrame
                    {
                        RawMethodName = r.ReadString(),
                        SourceFile = r.ReadString(),
                        SourceLine = r.ReadInt32()
                    });
                }
            }

            // Per-frame stats
            g.MeanBytesPerFrame = r.ReadDouble();
            g.MedianBytesPerFrame = r.ReadInt64();
            g.MinBytesPerFrame = r.ReadInt64();
            g.MaxBytesPerFrame = r.ReadInt64();
            g.MinFrame = r.ReadInt32();
            g.MaxFrame = r.ReadInt32();
            g.FirstFrame = r.ReadInt32();

            // TopWorst arrays
            int twCount = r.ReadInt32();
            if (twCount > 0)
            {
                g.TopWorstFrameIndices = new int[twCount];
                g.TopWorstFrameBytes = new long[twCount];
                for (int t = 0; t < twCount; t++)
                {
                    g.TopWorstFrameIndices[t] = r.ReadInt32();
                    g.TopWorstFrameBytes[t] = r.ReadInt64();
                }
            }

            // Pre-computed display strings
            g.FormattedBytes = r.ReadString();
            g.FormattedCount = r.ReadString();
            g.FormattedAvg = r.ReadString();
            g.FormattedPct = r.ReadString();
            g.RangeBytesPerFrame = r.ReadInt64();
            g.FormattedMedian = r.ReadString();
            g.FormattedMin = r.ReadString();
            g.FormattedMax = r.ReadString();
            g.FormattedMean = r.ReadString();
            g.FormattedRange = r.ReadString();
            g.FormattedFirst = r.ReadString();

            int ftwCount = r.ReadInt32();
            if (ftwCount > 0)
            {
                g.FormattedTopWorst = new string[ftwCount];
                for (int t = 0; t < ftwCount; t++)
                    g.FormattedTopWorst[t] = r.ReadString();
            }

            return g;
        }

        // ═══════════════════════════════════════════════════
        //  HEADER VALIDATION
        // ═══════════════════════════════════════════════════

        static void ReadAndValidateHeader(BinaryReader r)
        {
            int magic = r.ReadInt32();
            if (magic != k_GCAllocSnapshot)
                throw new InvalidDataException($"Invalid snapshot file (magic: 0x{magic:X8})");

            int version = r.ReadInt32();
            if (version != k_Version)
                throw new InvalidDataException($"Unsupported snapshot version: {version} (expected {k_Version})");
        }

        // ═══════════════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════════════

        static int Intern(string s, Dictionary<string, int> map, List<string> list)
        {
            s ??= "";
            if (map.TryGetValue(s, out int idx))
                return idx;
            idx = list.Count;
            map[s] = idx;
            list.Add(s);
            return idx;
        }

        static void PackInt32(byte[] buf, int off, int v)
        {
            buf[off] = (byte)v;
            buf[off + 1] = (byte)(v >> 8);
            buf[off + 2] = (byte)(v >> 16);
            buf[off + 3] = (byte)(v >> 24);
        }

        static void PackInt64(byte[] buf, int off, long v)
        {
            buf[off] = (byte)v;
            buf[off + 1] = (byte)(v >> 8);
            buf[off + 2] = (byte)(v >> 16);
            buf[off + 3] = (byte)(v >> 24);
            buf[off + 4] = (byte)(v >> 32);
            buf[off + 5] = (byte)(v >> 40);
            buf[off + 6] = (byte)(v >> 48);
            buf[off + 7] = (byte)(v >> 56);
        }

        static void PackUInt64(byte[] buf, int off, ulong v)
        {
            buf[off] = (byte)v;
            buf[off + 1] = (byte)(v >> 8);
            buf[off + 2] = (byte)(v >> 16);
            buf[off + 3] = (byte)(v >> 24);
            buf[off + 4] = (byte)(v >> 32);
            buf[off + 5] = (byte)(v >> 40);
            buf[off + 6] = (byte)(v >> 48);
            buf[off + 7] = (byte)(v >> 56);
        }

        static int UnpackInt32(byte[] buf, int off)
        {
            return buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24);
        }

        static long UnpackInt64(byte[] buf, int off)
        {
            return (long)buf[off] | ((long)buf[off + 1] << 8) | ((long)buf[off + 2] << 16) | ((long)buf[off + 3] << 24)
                | ((long)buf[off + 4] << 32) | ((long)buf[off + 5] << 40) | ((long)buf[off + 6] << 48) | ((long)buf[off + 7] << 56);
        }

        static ulong UnpackUInt64(byte[] buf, int off)
        {
            return (ulong)buf[off] | ((ulong)buf[off + 1] << 8) | ((ulong)buf[off + 2] << 16) | ((ulong)buf[off + 3] << 24)
                | ((ulong)buf[off + 4] << 32) | ((ulong)buf[off + 5] << 40) | ((ulong)buf[off + 6] << 48) | ((ulong)buf[off + 7] << 56);
        }

        // ═══════════════════════════════════════════════════
        //  ROUND-TRIP VERIFICATION
        // ═══════════════════════════════════════════════════

        public static bool VerifyRoundTrip(AnalysisSnapshot original, GraphFrameStore originalStore)
        {
            string tempPath = FileUtil.GetUniqueTempPathInProject() + ".gcaverify";
            try
            {
                Write(tempPath, original, originalStore);
                var restored = Read(tempPath, out var restoredStore);
                return CompareSnapshots(original, restored, originalStore, restoredStore);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SnapshotSerializer] Round-trip verification failed with exception: {ex.Message}");
                return false;
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        static bool CompareSnapshots(AnalysisSnapshot a, AnalysisSnapshot b,
            GraphFrameStore storeA, GraphFrameStore storeB)
        {
            var sb = new System.Text.StringBuilder(512);
            int issues = 0;
            const int k_MaxIssues = 10;

            void Fail(string msg) { if (issues < k_MaxIssues) sb.AppendLine(msg); issues++; }

            if (a.TotalBytes != b.TotalBytes) Fail($"TotalBytes: {a.TotalBytes} vs {b.TotalBytes}");
            if (a.TotalCount != b.TotalCount) Fail($"TotalCount: {a.TotalCount} vs {b.TotalCount}");
            if (a.FrameStart != b.FrameStart) Fail($"FrameStart: {a.FrameStart} vs {b.FrameStart}");
            if (a.FrameEnd != b.FrameEnd) Fail($"FrameEnd: {a.FrameEnd} vs {b.FrameEnd}");
            if (a.HadCallStacks != b.HadCallStacks) Fail($"HadCallStacks: {a.HadCallStacks} vs {b.HadCallStacks}");

            if (a.RawAllocations.Count != b.RawAllocations.Count)
            {
                Fail($"Alloc count: {a.RawAllocations.Count} vs {b.RawAllocations.Count}");
                Debug.LogError($"[SnapshotSerializer] Round-trip FAILED ({issues} issues):\n{sb}");
                return false;
            }

            for (int i = 0; i < a.RawAllocations.Count && issues < k_MaxIssues; i++)
                CompareAlloc(a.RawAllocations[i], b.RawAllocations[i], i, Fail);

            // Compare store
            if (storeA != null && storeB != null)
            {
                if (storeA.FullFrameStart != storeB.FullFrameStart) Fail("Store.FullFrameStart mismatch");
                if (storeA.FullFrameEnd != storeB.FullFrameEnd) Fail("Store.FullFrameEnd mismatch");
                int lenA = storeA.FullFrameBytes?.Length ?? 0;
                int lenB = storeB.FullFrameBytes?.Length ?? 0;
                if (lenA != lenB)
                    Fail($"Store.FullFrameBytes length: {lenA} vs {lenB}");
                else
                {
                    for (int i = 0; i < lenA; i++)
                    {
                        if (storeA.FullFrameBytes[i] != storeB.FullFrameBytes[i])
                        {
                            Fail($"Store.FullFrameBytes[{i}]: {storeA.FullFrameBytes[i]} vs {storeB.FullFrameBytes[i]}");
                            break;
                        }
                    }
                }
            }

            if (issues > 0)
            {
                if (issues > k_MaxIssues)
                    sb.AppendLine($"... and {issues - k_MaxIssues} more");
                Debug.LogError($"[SnapshotSerializer] Round-trip FAILED ({issues} issues):\n{sb}");
                return false;
            }

            Debug.Log($"[SnapshotSerializer] Round-trip verification PASSED: {a.RawAllocations.Count} allocations");
            return true;
        }

        static void CompareAlloc(RawAllocation a, RawAllocation b, int idx, Action<string> fail)
        {
            if (a.Bytes != b.Bytes) { fail($"Alloc[{idx}].Bytes: {a.Bytes} vs {b.Bytes}"); return; }
            if (a.FrameIndex != b.FrameIndex) { fail($"Alloc[{idx}].FrameIndex: {a.FrameIndex} vs {b.FrameIndex}"); return; }
            if (a.RawSampleIndex != b.RawSampleIndex) { fail($"Alloc[{idx}].RawSampleIndex"); return; }
            if (a.ThreadId != b.ThreadId) { fail($"Alloc[{idx}].ThreadId"); return; }
            if (a.ThreadIndex != b.ThreadIndex) { fail($"Alloc[{idx}].ThreadIndex"); return; }
            if (a.FullCallstackId != b.FullCallstackId) { fail($"Alloc[{idx}].FullCallstackId"); return; }
            if (a.TopFrameId != b.TopFrameId) { fail($"Alloc[{idx}].TopFrameId"); return; }
            if (a.FullCallstackGroupIndex != b.FullCallstackGroupIndex) { fail($"Alloc[{idx}].FullCallstackGroupIndex"); return; }
            if (a.TopFrameGroupIndex != b.TopFrameGroupIndex) { fail($"Alloc[{idx}].TopFrameGroupIndex"); return; }
            if (a.ThreadAllocCountIndex != b.ThreadAllocCountIndex) { fail($"Alloc[{idx}].ThreadAllocCountIndex"); return; }
            if ((a.ThreadDisplayName ?? "") != (b.ThreadDisplayName ?? "")) { fail($"Alloc[{idx}].ThreadDisplayName"); return; }
            if ((a.ThreadName ?? "") != (b.ThreadName ?? "")) { fail($"Alloc[{idx}].ThreadName"); return; }
            if ((a.ThreadGroupName ?? "") != (b.ThreadGroupName ?? "")) { fail($"Alloc[{idx}].ThreadGroupName"); return; }
            if ((a.ParentMethod ?? "") != (b.ParentMethod ?? "")) { fail($"Alloc[{idx}].ParentMethod"); return; }
            if ((a.HierarchyPath ?? "") != (b.HierarchyPath ?? "")) { fail($"Alloc[{idx}].HierarchyPath"); return; }
            if ((a.FullCallstackKey ?? "") != (b.FullCallstackKey ?? "")) { fail($"Alloc[{idx}].FullCallstackKey"); return; }
            if ((a.TopFrameKey ?? "") != (b.TopFrameKey ?? "")) { fail($"Alloc[{idx}].TopFrameKey"); return; }
            if ((a.DisplayName ?? "") != (b.DisplayName ?? "")) { fail($"Alloc[{idx}].DisplayName"); return; }
            if ((a.DisplayNameWithAssembly ?? "") != (b.DisplayNameWithAssembly ?? "")) { fail($"Alloc[{idx}].DisplayNameWithAssembly"); return; }
            if ((a.FormattedBytes ?? "") != (b.FormattedBytes ?? "")) { fail($"Alloc[{idx}].FormattedBytes"); return; }
            if ((a.FormattedFrame ?? "") != (b.FormattedFrame ?? "")) { fail($"Alloc[{idx}].FormattedFrame"); return; }

            int csA = a.ResolvedCallStack?.Count ?? 0;
            int csB = b.ResolvedCallStack?.Count ?? 0;
            if (csA != csB) { fail($"Alloc[{idx}].ResolvedCallStack.Count: {csA} vs {csB}"); return; }
            for (int f = 0; f < csA; f++)
            {
                if ((a.ResolvedCallStack[f].RawMethodName ?? "") != (b.ResolvedCallStack[f].RawMethodName ?? "")) { fail($"Alloc[{idx}].CS[{f}].RawMethodName"); return; }
                if ((a.ResolvedCallStack[f].SourceFile ?? "") != (b.ResolvedCallStack[f].SourceFile ?? "")) { fail($"Alloc[{idx}].CS[{f}].SourceFile"); return; }
                if (a.ResolvedCallStack[f].SourceLine != b.ResolvedCallStack[f].SourceLine) { fail($"Alloc[{idx}].CS[{f}].SourceLine"); return; }
            }
        }
    }
}
