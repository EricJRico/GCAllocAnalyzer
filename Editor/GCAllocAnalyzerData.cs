using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GCAllocBreakdown.Editor
{
    // ═══════════════════════════════════════════════════
    //  ANALYSIS SNAPSHOT — serializable data container
    // ═══════════════════════════════════════════════════

    [Serializable]
    internal class AnalysisSnapshot
    {
        public List<RawAllocation> RawAllocations = new(4096);
        public List<string> SortedThreadNames = new(32);
        public long TotalBytes;
        public int TotalCount;
        public int FrameStart;
        public int FrameEnd;
        public bool HadCallStacks;

        [NonSerialized] public List<CallsiteGroup> GroupsByFullCallstack = new(256);
        [NonSerialized] public List<CallsiteGroup> GroupsByTopFrame = new(256);
        [NonSerialized] public long[] PerFrameBytes;

        public bool HasData => RawAllocations != null && RawAllocations.Count > 0;

        public void EnsureNonSerializedLists()
        {
            GroupsByFullCallstack ??= new List<CallsiteGroup>(256);
            GroupsByTopFrame ??= new List<CallsiteGroup>(256);
        }
    }

    // ═══════════════════════════════════════════════════
    //  DATA STRUCTURES
    // ═══════════════════════════════════════════════════

    [Serializable]
    internal class RawAllocation
    {
        public long Bytes;
        public int FrameIndex;
        public int RawSampleIndex;
        public string ThreadDisplayName;
        public string ThreadName;
        public string ThreadGroupName;
        public ulong ThreadId;
        public int ThreadIndex;
        public string ParentMethod;
        public string HierarchyPath;
        public List<ResolvedFrame> ResolvedCallStack;

        // Pre-computed keys (built once during analysis)
        public string FullCallstackKey;
        public string TopFrameKey;
        public string DisplayName;
        public string DisplayNameWithAssembly;

        // Pre-computed display strings (built once during analysis)
        public string FormattedBytes;
        public string FormattedFrame;
    }

    [Serializable]
    internal class CallsiteGroup
    {
        public string Key;
        public string DisplayName;
        public long TotalBytes;
        public int Count;
        public float Percentage;
        public List<ResolvedFrame> ResolvedCallStack;
        public List<RawAllocation> Allocations;

        // Per-frame statistics (computed during grouping)
        public double MeanBytesPerFrame;
        public long MedianBytesPerFrame;
        public long MinBytesPerFrame;
        public long MaxBytesPerFrame;
        public int MinFrame;
        public int MaxFrame;
        public int FirstFrame;
        public int[] TopWorstFrameIndices;   // up to 3, descending by bytes
        public long[] TopWorstFrameBytes;     // parallel array, same length

        // Pre-computed display strings (built once during grouping)
        public string FormattedBytes;
        public string FormattedCount;
        public string FormattedAvg;
        public string FormattedPct;
        public long RangeBytesPerFrame;    // MaxBytesPerFrame - MinBytesPerFrame
        public string FormattedMedian;
        public string FormattedMin;
        public string FormattedMax;
        public string FormattedMean;
        public string FormattedRange;
        public string FormattedFirst;
        public string[] FormattedTopWorst;    // pre-built "frame N — X KB" strings
    }

    [Serializable]
    internal struct ResolvedFrame
    {
        public string RawMethodName;
        public string SourceFile;
        public int SourceLine;
    }

    internal class DepthEntry
    {
        public string Name;
        public int MarkerId;
        public int Remaining;
    }

    // ═══════════════════════════════════════════════════
    //  STATIC UTILITIES
    // ═══════════════════════════════════════════════════

    internal static class GCAllocUtils
    {
        /// <summary>
        /// Converts a 0-based profiler API index to the 1-based frame number
        /// shown in Unity's Profiler window.  Use at every display boundary.
        /// </summary>
        public static int DisplayFrame(int frameIndex) => frameIndex + 1;

        /// <summary>
        /// Converts a 1-based display frame number back to the 0-based index
        /// expected by ProfilerDriver / selectedFrameIndex.  Use when reading
        /// user-facing input fields.
        /// </summary>
        public static int ApiFrame(int displayFrame) => displayFrame - 1;

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return string.Concat((bytes / (1024f * 1024f)).ToString("F1"), " MB");
            if (bytes >= 1024)
                return string.Concat((bytes / 1024f).ToString("F1"), " KB");
            return string.Concat(bytes.ToString(), " B");
        }

        public static string ExtractAssembly(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int bang = raw.IndexOf('!');
            return bang > 0 ? raw.Substring(0, bang) : "";
        }

        public static string StripAssembly(string raw)
        {
            int bang = raw != null ? raw.IndexOf('!') : -1;
            string result = bang >= 0 ? raw.Substring(bang + 1) : raw ?? "";
            if (result.Length > 2 && result[0] == ':' && result[1] == ':')
                result = result.Substring(2);
            return result;
        }

        /// <summary>
        /// Keeps the assembly prefix but strips a leading :: after the ! separator.
        /// e.g. "mscorlib!::String.Concat" → "mscorlib!String.Concat"
        /// </summary>
        public static string StripLeadingColons(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw ?? "";
            int bang = raw.IndexOf('!');
            if (bang < 0) return raw;
            int afterBang = bang + 1;
            if (afterBang + 1 < raw.Length && raw[afterBang] == ':' && raw[afterBang + 1] == ':')
                return string.Concat(raw.Substring(0, afterBang), raw.Substring(afterBang + 2));
            return raw;
        }

        public static string FormatTopFrame(ResolvedFrame frame)
        {
            string name = StripAssembly(frame.RawMethodName);

            if (!string.IsNullOrEmpty(frame.SourceFile))
            {
                string fn = Path.GetFileName(frame.SourceFile);
                return frame.SourceLine > 0
                    ? string.Concat(name, "  —  ", fn, ":", frame.SourceLine.ToString())
                    : string.Concat(name, "  —  ", fn);
            }
            return name;
        }

        public static string FormatTopFrameWithAssembly(ResolvedFrame frame)
        {
            string name = StripLeadingColons(frame.RawMethodName);

            if (!string.IsNullOrEmpty(frame.SourceFile))
            {
                string fn = Path.GetFileName(frame.SourceFile);
                return frame.SourceLine > 0
                    ? string.Concat(name, "  —  ", fn, ":", frame.SourceLine.ToString())
                    : string.Concat(name, "  —  ", fn);
            }
            return name;
        }

        public static string NormalizeKeyPart(ResolvedFrame f)
        {
            string method = StripAssembly(f.RawMethodName);
            string file = !string.IsNullOrEmpty(f.SourceFile) ? Path.GetFileName(f.SourceFile) : "";
            return f.SourceLine > 0
                ? string.Concat(method, "@", file, ":", f.SourceLine.ToString())
                : string.Concat(method, "@", file);
        }

        public static void AppendNormalizedKeyPart(StringBuilder sb, ResolvedFrame f)
        {
            sb.Append(StripAssembly(f.RawMethodName));
            sb.Append('@');
            if (!string.IsNullOrEmpty(f.SourceFile))
                sb.Append(Path.GetFileName(f.SourceFile));
            if (f.SourceLine > 0) { sb.Append(':'); sb.Append(f.SourceLine); }
        }

        public static string EscapeCsvField(string field)
        {
            if (field == null) return "";
            if (field.IndexOf(',') < 0 && field.IndexOf('"') < 0 &&
                field.IndexOf('\n') < 0 && field.IndexOf('\r') < 0)
                return field;
            return string.Concat("\"", field.Replace("\"", "\"\""), "\"");
        }
    }

    // ═══════════════════════════════════════════════════
    //  GRAPH FRAME STORE — full-range data + analysis cache
    //  Source of truth for the graph; separate from
    //  AnalysisSnapshot which represents the current view.
    // ═══════════════════════════════════════════════════

    [Serializable]
    internal class GraphFrameStore
    {
        public int FullFrameStart;
        public int FullFrameEnd;
        public long[] FullFrameBytes;              // per-frame GC totals for entire profiler range

        [NonSerialized] public List<RawAllocation> CachedRawAllocations;
        [NonSerialized] public List<string> CachedSortedThreadNames;

        public bool HasFullFrameData => FullFrameBytes != null && FullFrameBytes.Length > 0;
        public bool HasCachedAnalysis => CachedRawAllocations != null && CachedRawAllocations.Count > 0;

        public int FullFrameCount => HasFullFrameData ? FullFrameEnd - FullFrameStart + 1 : 0;

        public void Clear()
        {
            FullFrameStart = 0;
            FullFrameEnd = 0;
            FullFrameBytes = null;
            CachedRawAllocations = null;
            CachedSortedThreadNames = null;
        }

        public void CacheAnalysis(List<RawAllocation> rawAllocations, List<string> sortedThreadNames)
        {
            // Copy the list so the cache is independent of the snapshot.
            // RawAllocation references are shared but immutable after creation.
            CachedRawAllocations = new List<RawAllocation>(rawAllocations.Count);
            for (int i = 0; i < rawAllocations.Count; i++)
                CachedRawAllocations.Add(rawAllocations[i]);

            CachedSortedThreadNames = new List<string>(sortedThreadNames.Count);
            for (int i = 0; i < sortedThreadNames.Count; i++)
                CachedSortedThreadNames.Add(sortedThreadNames[i]);
        }

        // Non-serialized fields (CachedRawAllocations, CachedSortedThreadNames) are
        // intentionally not restored after domain reload — a fresh Analyze is needed.
    }
}
