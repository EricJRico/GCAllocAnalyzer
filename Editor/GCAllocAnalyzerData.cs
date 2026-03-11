using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace GCAllocBreakdown.Editor
{
    // ═══════════════════════════════════════════════════
    //  ANALYSIS SNAPSHOT — serializable data container
    // ═══════════════════════════════════════════════════

    [Serializable]
    internal class AnalysisSnapshot
    {
        [NonSerialized] public List<RawAllocation> RawAllocations = new(4096);
        public List<string> SortedThreadNames = new(32);
        public long TotalBytes;
        public int TotalCount;
        public int FrameStart;
        public int FrameEnd;
        public bool HadCallStacks;

        [NonSerialized] public List<CallsiteGroup> GroupsByFullCallstack = new(256);
        [NonSerialized] public List<CallsiteGroup> GroupsByTopFrame = new(256);
        [NonSerialized] public long[] PerFrameBytes;

        /// <summary>
        /// True when skeleton metadata exists (from analysis or file restore).
        /// RawAllocations may still be loading on a background thread.
        /// </summary>
        public bool HasData => TotalCount > 0;

        public void EnsureNonSerializedLists()
        {
            RawAllocations ??= new List<RawAllocation>(4096);
            GroupsByFullCallstack ??= new List<CallsiteGroup>(256);
            GroupsByTopFrame ??= new List<CallsiteGroup>(256);
        }

        public bool HasRawAllocations => RawAllocations != null && RawAllocations.Count > 0;
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

        // Group indices stamped during initial BuildGrouping — enables fast
        // integer-keyed regrouping on sub-range selection (avoids rehashing long keys).
        public int FullCallstackGroupIndex = -1;
        public int TopFrameGroupIndex = -1;

        // Unique integer IDs for grouping keys — stamped during extraction to
        // replace string-keyed dictionary lookups in BuildGrouping with O(1) array indexing.
        // FullCallstackId: one per unique CachedCallStack/CachedDepthInfo entry.
        // TopFrameId: one per unique TopFrameKey (shared across callstacks with same top frame).
        public int FullCallstackId = -1;
        public int TopFrameId = -1;

        // Dense thread index stamped during initial extraction — enables int[]
        // counting in sub-range rebuilds instead of Dictionary<string,int> lookups.
        public int ThreadAllocCountIndex = -1;

        // Method palette index stamped by MethodPalette.Build() — enables flat array
        // indexing in BuildSegmentData instead of per-allocation string dictionary lookups.
        [NonSerialized] public int SegmentMethodIndex = -1;

        // Pre-computed display strings (built once during analysis)
        public string FormattedBytes;
        public string FormattedFrame;
    }

    [Serializable]
    internal class CallsiteGroup
    {
        public string Key;
        public string DisplayName;
        public string DisplayNameNoAssembly;
        public string DisplayNameWithAssembly;
        public string HierarchyPath;
        public int GroupIndex;
        public long TotalBytes;
        public int Count;
        public float Percentage;
        public List<ResolvedFrame> ResolvedCallStack;

        // First allocation info — for CPU module selection and display name swap
        public int FirstAllocFrameIndex;
        public int FirstAllocRawSampleIndex;
        public string FirstAllocThreadName;
        public string FirstAllocThreadGroupName;
        public ulong FirstAllocThreadId;

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
    //  STACKED BAR SEGMENT DATA
    // ═══════════════════════════════════════════════════

    internal struct BarSegment
    {
        public int MethodIndex;   // index into MethodColorPalette (k_OthersIndex = "Others")
        public long Bytes;
    }

    internal class MethodColorPalette
    {
        public const int k_OthersIndex = -1;

        static readonly Color[] k_Palette =
        {
            new(0.88f, 0.38f, 0.38f), // Red
            new(0.88f, 0.56f, 0.25f), // Orange
            new(0.82f, 0.69f, 0.25f), // Gold
            new(0.50f, 0.75f, 0.31f), // Lime
            new(0.31f, 0.69f, 0.44f), // Green
            new(0.25f, 0.69f, 0.63f), // Teal-green
            new(0.25f, 0.56f, 0.75f), // Sky blue
            new(0.31f, 0.44f, 0.82f), // Blue
            new(0.44f, 0.38f, 0.82f), // Indigo
            new(0.56f, 0.31f, 0.75f), // Purple
            new(0.69f, 0.31f, 0.63f), // Magenta
            new(0.82f, 0.31f, 0.50f), // Pink
            new(0.75f, 0.44f, 0.38f), // Salmon
            new(0.50f, 0.63f, 0.38f), // Olive
            new(0.38f, 0.56f, 0.63f), // Slate
            new(0.63f, 0.50f, 0.38f), // Tan
        };

        static readonly Color k_OthersColor = new(0.45f, 0.45f, 0.45f);

        readonly Dictionary<string, int> m_MethodToIndex = new(64);
        readonly List<string> m_IndexToMethod = new(64);

        public int Count => m_IndexToMethod.Count;

        readonly Dictionary<string, long> m_BytesPerMethod = new(64);
        readonly List<KeyValuePair<string, long>> m_SortedForBuild = new(64);

        public void Build(List<RawAllocation> allocs)
        {
            m_MethodToIndex.Clear();
            m_IndexToMethod.Clear();
            m_BytesPerMethod.Clear();
            m_SortedForBuild.Clear();

            // Sum bytes per display name (human-readable top-frame method).
            // DisplayName when call stacks are enabled, ParentMethod otherwise.
            for (int i = 0; i < allocs.Count; i++)
            {
                string key = allocs[i].DisplayName;
                if (string.IsNullOrEmpty(key))
                    key = allocs[i].ParentMethod;
                if (string.IsNullOrEmpty(key))
                    key = "(unknown)";
                if (m_BytesPerMethod.TryGetValue(key, out long existing))
                    m_BytesPerMethod[key] = existing + allocs[i].Bytes;
                else
                    m_BytesPerMethod[key] = allocs[i].Bytes;
            }

            // Sort descending by total bytes
            foreach (var kvp in m_BytesPerMethod)
                m_SortedForBuild.Add(kvp);
            m_SortedForBuild.Sort((a, b) => b.Value.CompareTo(a.Value));

            // Assign palette indices in rank order
            for (int i = 0; i < m_SortedForBuild.Count; i++)
            {
                m_MethodToIndex[m_SortedForBuild[i].Key] = i;
                m_IndexToMethod.Add(m_SortedForBuild[i].Key);
            }

            // Stamp SegmentMethodIndex on each allocation for O(1) array indexing
            // in BuildSegmentData (avoids 1.87M string dictionary lookups).
            for (int i = 0; i < allocs.Count; i++)
            {
                string key = allocs[i].DisplayName;
                if (string.IsNullOrEmpty(key))
                    key = allocs[i].ParentMethod;
                if (string.IsNullOrEmpty(key))
                    key = "(unknown)";
                allocs[i].SegmentMethodIndex = m_MethodToIndex.TryGetValue(key, out int idx) ? idx : k_OthersIndex;
            }
        }

        public int GetIndex(string method)
        {
            return m_MethodToIndex.TryGetValue(method, out int idx) ? idx : k_OthersIndex;
        }

        public Color GetColor(int methodIndex)
        {
            if (methodIndex == k_OthersIndex) return k_OthersColor;
            return k_Palette[methodIndex % k_Palette.Length];
        }

        public string GetMethodName(int methodIndex)
        {
            if (methodIndex == k_OthersIndex || methodIndex < 0 || methodIndex >= m_IndexToMethod.Count)
                return "Others";
            return m_IndexToMethod[methodIndex];
        }

        public void Clear()
        {
            m_MethodToIndex.Clear();
            m_IndexToMethod.Clear();
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

        // These are rebuilt from m_Snapshot in TryRestoreAfterReload — NOT serialized
        // to avoid duplicating 1.87M+ RawAllocation objects (Unity serializes by value,
        // not reference, so duplicated lists would multiply serialization cost 4×).
        [NonSerialized] public List<RawAllocation> CachedRawAllocations;
        [NonSerialized] public List<string> CachedSortedThreadNames;

        // Group templates from initial full-range analysis — used by single-pass
        // sub-range regrouping to avoid string dictionary lookups.
        [NonSerialized] public List<CallsiteGroup> CachedGroupsByFullCallstack;
        [NonSerialized] public List<CallsiteGroup> CachedGroupsByTopFrame;

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
            CachedGroupsByFullCallstack = null;
            CachedGroupsByTopFrame = null;
        }

        public void CacheAnalysis(List<RawAllocation> rawAllocations, List<string> sortedThreadNames,
            List<CallsiteGroup> groupsByFullCallstack, List<CallsiteGroup> groupsByTopFrame)
        {
            // Copy the list so the cache is independent of the snapshot.
            // RawAllocation references are shared but immutable after creation.
            CachedRawAllocations = new List<RawAllocation>(rawAllocations.Count);
            for (int i = 0; i < rawAllocations.Count; i++)
                CachedRawAllocations.Add(rawAllocations[i]);

            CachedSortedThreadNames = new List<string>(sortedThreadNames.Count);
            for (int i = 0; i < sortedThreadNames.Count; i++)
                CachedSortedThreadNames.Add(sortedThreadNames[i]);

            CachedGroupsByFullCallstack = new List<CallsiteGroup>(groupsByFullCallstack);
            CachedGroupsByTopFrame = new List<CallsiteGroup>(groupsByTopFrame);
        }

        // CachedRawAllocations and CachedSortedThreadNames are rebuilt from
        // m_Snapshot after domain reload in TryRestoreAfterReload.
    }

    // ═══════════════════════════════════════════════════
    //  GRAPH CONTROLLER STATE — serializable subset of
    //  PerFrameGraphController state that must survive
    //  domain reload.
    // ═══════════════════════════════════════════════════

    [Serializable]
    internal struct GraphControllerState
    {
        public bool OrderByMagnitude;
        public float ViewportStart;
        public float ViewportEnd;
        public bool HasFrameSelection;
        [System.NonSerialized] public bool[] SelectedFrameBuffer;
        public int SelectedFrameBaseFrame;
        public int SelectionFrameStart;
        public int SelectionFrameEnd;
        public int HighlightedFrame;
        public long UserYAxisMax;
        public bool HasCustomYScale;
        public long YPanOffset;

        public static GraphControllerState Default => new GraphControllerState
        {
            OrderByMagnitude = false,
            ViewportStart = 0f,
            ViewportEnd = 1f,
            HasFrameSelection = false,
            SelectionFrameStart = -1,
            SelectionFrameEnd = -1,
            HighlightedFrame = -1,
            UserYAxisMax = 0,
            HasCustomYScale = false,
            YPanOffset = 0
        };
    }
}
