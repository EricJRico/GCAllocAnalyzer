using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace GCAllocBreakdown.Editor
{
    public class GCAllocAnalyzerWindow : EditorWindow
    {
        // ═══════════════════════════════════════════════════
        //  CONSTANTS
        // ═══════════════════════════════════════════════════

        const float COL_BYTES = 80;
        const float COL_COUNT = 55;
        const float COL_AVG = 70;
        const float COL_PCT = 50;
        const float COL_MEDIAN = 60;
        const float COL_MEAN   = 60;
        const float COL_MIN    = 55;
        const float COL_MAX    = 55;
        const float COL_RANGE  = 55;
        const float COL_FIRST  = 45;
        const float ALLOC_COL_NUM = 30;
        const float ALLOC_COL_SIZE = 70;
        const float ALLOC_COL_FRAME = 60;
        const int MARKER_ROW_HEIGHT = 22;
        const int ALLOC_ROW_HEIGHT = 20;
        const int MAX_CALLSTACK_FRAMES = 20;
        const string k_MainThread = "Main Thread";
        const string k_RenderThread = "Render Thread";

        // Marker table column names
        const string k_ColSite = "site", k_ColBytes = "bytes", k_ColCount = "count";
        const string k_ColAvg = "avg", k_ColPct = "pct", k_ColMedian = "median";
        const string k_ColMean = "mean", k_ColMin = "min", k_ColMax = "max";
        const string k_ColRange = "range", k_ColFirst = "first";
        // Alloc table column names
        const string k_AllocColNum = "num", k_AllocColSize = "size";
        const string k_AllocColFrame = "frame", k_AllocColThread = "thread";

        // Cached column-header factories — lambdas capture only literals, so the
        // compiler emits them as static cached delegates (zero per-call allocation).
        static readonly Func<VisualElement> k_HeaderSite    = () => new Label("Allocation Site") { tooltip = "Method or call site where the GC allocation occurred" };
        static readonly Func<VisualElement> k_HeaderBytes   = () => new Label("Bytes")           { tooltip = "Total bytes allocated by this call site across all analyzed frames" };
        static readonly Func<VisualElement> k_HeaderCount   = () => new Label("Count")           { tooltip = "Number of individual GC.Alloc events from this call site" };
        static readonly Func<VisualElement> k_HeaderAvg     = () => new Label("Avg")             { tooltip = "Average bytes per allocation (Total Bytes \u00f7 Count)" };
        static readonly Func<VisualElement> k_HeaderPct     = () => new Label("%")               { tooltip = "Percentage of total GC allocation bytes" };
        static readonly Func<VisualElement> k_HeaderMedian  = () => new Label("Median")          { tooltip = "Median bytes per frame (frames with allocations only)" };
        static readonly Func<VisualElement> k_HeaderMean    = () => new Label("Mean")            { tooltip = "Mean bytes per frame across all analyzed frames" };
        static readonly Func<VisualElement> k_HeaderMin     = () => new Label("Min")             { tooltip = "Minimum bytes allocated in any single frame" };
        static readonly Func<VisualElement> k_HeaderMax     = () => new Label("Max")             { tooltip = "Maximum bytes allocated in any single frame" };
        static readonly Func<VisualElement> k_HeaderRange   = () => new Label("Range")           { tooltip = "Difference between max and min bytes per frame" };
        static readonly Func<VisualElement> k_HeaderFirst   = () => new Label("First")           { tooltip = "First frame where this call site allocated" };
        static readonly Func<VisualElement> k_HeaderAllocNum    = () => new Label("#")       { tooltip = "Allocation index" };
        static readonly Func<VisualElement> k_HeaderAllocSize   = () => new Label("Size")    { tooltip = "Size of this individual allocation" };
        static readonly Func<VisualElement> k_HeaderAllocFrame  = () => new Label("Frame")   { tooltip = "Profiler frame number where this allocation occurred" };
        static readonly Func<VisualElement> k_HeaderAllocThread = () => new Label("Thread")  { tooltip = "Thread where this allocation occurred" };

        static readonly Color k_TopFrame = new(0.9f, 0.9f, 0.6f);
        static readonly Color k_CallerFrame = new(0.55f, 0.55f, 0.55f);
        static readonly Color k_SubtleText = new(0.5f, 0.5f, 0.5f);
        static readonly Color k_HoverBg = new(0.3f, 0.3f, 0.3f);

        // ═══════════════════════════════════════════════════
        //  UI FIELDS
        // ═══════════════════════════════════════════════════

        IntegerField m_StartFrameField;
        IntegerField m_EndFrameField;
        Label m_FrameRangeInfo;
        Foldout m_FiltersFoldout;
        TextField m_NameFilter;
        TextField m_ExcludeFilter;
        Button m_ThreadFilterBtn;
        Toggle m_GroupByCallsite;
        MultiColumnListView m_MarkerListView;
        Label m_StatusLabel;
        Button m_SaveBtn;
        Button m_LoadBtn;
        Button m_ExportBtn;
        Label m_LoadedSnapshotLabel;

        // Per-frame graph — delegated to PerFrameGraphController
        PerFrameGraphController m_GraphController;

        // Right panel
        Label m_FrameCountLabel, m_FrameRangeLabel, m_TotalGcLabel;
        Label m_TotalAllocsLabel, m_UniqueSitesLabel;
        Label m_MarkerNameLabel, m_MarkerSourceLabel, m_MarkerStatsLabel;
        VisualElement m_CallStackContainer;
        ScrollView m_CallStackScroll;
        MultiColumnListView m_AllocListView;
        VisualElement m_MarkerSummaryRoot;
        Label m_NoDataLabel;

        // Top Offenders
        Foldout m_TopOffendersFoldout;
        VisualElement m_TopByTotalContainer;
        VisualElement m_TopSpikesContainer;
        readonly List<CallsiteGroup> m_TopByTotalBytes = new(10);
        readonly List<RawAllocation> m_TopSingleAllocs = new(10);

        // Alloc sort
        enum AllocSortCol { Size, Frame }
        AllocSortCol m_AllocSortCol = AllocSortCol.Size;
        bool m_AllocSortAsc;

        // Loaded snapshot — Profiler sync is invalid for loaded snapshots
        [SerializeField] bool m_IsLoadedSnapshot;

        // ═══════════════════════════════════════════════════
        //  SORT
        // ═══════════════════════════════════════════════════

        enum SortCol { Bytes = 0, Count = 1, Avg = 2, Pct = 3, Name = 4,
            Median = 5, Mean = 6, Min = 7, Max = 8, Range = 9, First = 10 }

        // ═══════════════════════════════════════════════════
        //  DATA FIELDS — pre-allocated, reused via .Clear()
        // ═══════════════════════════════════════════════════

        // Core analysis data — serialized to survive domain reload
        [SerializeField] AnalysisSnapshot m_Snapshot = new();
        [SerializeField] GraphFrameStore m_FrameStore = new();
        [SerializeField] SortCol m_SortCol = SortCol.Bytes;
        [SerializeField] bool m_SortAsc;
        [SerializeField] bool m_ShowAssembly;
        [SerializeField] GraphControllerState m_GraphState = GraphControllerState.Default;
        [SerializeField] string m_NameFilterText = "";
        [SerializeField] string m_ExcludeFilterText = "";
        [SerializeField] bool m_GroupByCallsiteValue = true;
        [SerializeField] string[] m_SerializedSelectedThreads = Array.Empty<string>();
        [SerializeField] int m_SelectedMarkerIndex = -1;

        // Points to m_Snapshot.GroupsByFullCallstack or GroupsByTopFrame
        List<CallsiteGroup> m_ActiveGroups;

        readonly List<CallsiteGroup> m_FilteredGroups = new(256);
        List<RawAllocation> m_SelectedAllocations = new List<RawAllocation>(512);

        // Thread state (rebuilt from m_Snapshot.SortedThreadNames after reload)
        readonly HashSet<string> m_AllThreadNames = new();
        readonly HashSet<string> m_SelectedThreads = new();
        readonly Dictionary<string, int> m_ThreadAllocCounts = new(32);

        // Thread index: group key → set of thread names (built once per grouping)
        readonly Dictionary<string, HashSet<string>> m_GroupThreadIndex = new(256);

        // Reusable buffers
        readonly StringBuilder m_SharedSB = new(1024);
        readonly List<ulong> m_AddrBuffer = new(64);
        readonly List<ResolvedFrame> m_FrameBuffer = new(64);
        readonly List<DepthEntry> m_DepthStack = new(32);

        // ResolveMethodInfo cache — same address returns the same symbol info
        // within a profiler session. Cleared at the start of each RunAnalysis().
        readonly Dictionary<ulong, FrameDataView.MethodInfo> m_MethodInfoCache = new(4096);

        // Timing instrumentation
        readonly Stopwatch m_SwTotal = new();
        readonly Stopwatch m_SwThreadScan = new();
        readonly Stopwatch m_SwExtraction = new();
        readonly Stopwatch m_SwFrameDataView = new();
        readonly Stopwatch m_SwSampleIteration = new();
        readonly Stopwatch m_SwResolveMethod = new();
        readonly Stopwatch m_SwObjectAlloc = new();
        readonly Stopwatch m_SwGrouping = new();
        readonly Stopwatch m_SwPerFrame = new();
        long m_ResolveCallCount;
        long m_ResolveCacheHits;

        // Call stack deduplication — most allocations share the same few hundred
        // unique stacks.  We cache the resolved frames + derived strings once per
        // unique stack and share references across all matching allocations.
        struct CachedCallStack
        {
            public List<ResolvedFrame> Frames;
            public string FullCallstackKey;
            public string TopFrameKey;
            public string DisplayName;
            public string DisplayNameWithAssembly;
        }
        readonly Dictionary<long, CachedCallStack> m_CallStackCache = new(512);
        readonly Dictionary<long, string> m_FormattedBytesCache = new(256);
        string[] m_FormattedFrameStrs;
        static readonly List<ResolvedFrame> k_EmptyFrameList = new(0);
        long m_StackCacheHits;

        // Grouping work buffers
        readonly Dictionary<string, CallsiteGroup> m_GroupingDict = new(256);
        // Scratch buffer for ComputePerFrameStats (single-threaded, never concurrent)
        long[] m_PerFrameBuffer;

        // ═══════════════════════════════════════════════════
        //  PROFILER — lazy, only resolved on selection
        // ═══════════════════════════════════════════════════

        ProfilerWindow m_ProfilerWindow;
        IProfilerFrameTimeViewSampleSelectionController m_CpuController;
        readonly ScriptOpener m_ScriptOpener = new();

        // ═══════════════════════════════════════════════════
        //  WINDOW LIFECYCLE
        // ═══════════════════════════════════════════════════

        [MenuItem("Window/Analysis/GC Alloc Analyzer")]
        public static GCAllocAnalyzerWindow ShowWindow()
        {
            var w = GetWindow<GCAllocAnalyzerWindow>("GC Alloc Analyzer");
            w.minSize = new Vector2(1000, 420);
            w.Show();
            return w;
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexGrow = 1;
            root.style.minWidth = 950;

            root.Add(BuildToolbar());

            var splitView = new TwoPaneSplitView(1, 350, TwoPaneSplitViewOrientation.Horizontal);
            splitView.style.flexGrow = 1;
            splitView.Add(BuildLeftPanel());
            splitView.Add(BuildRightPanel());
            root.Add(splitView);

            root.Add(BuildStatusBar());
            ShowNoDataState(true);

            GCAllocSettings.SettingsChanged += OnSettingsChanged;

            // Restore state after domain reload (e.g. script save)
            TryRestoreAfterReload();
        }

        void OnDisable()
        {
            GCAllocSettings.SettingsChanged -= OnSettingsChanged;

            m_GraphState = m_GraphController.CaptureState();
            m_NameFilterText = m_NameFilter.value;
            m_ExcludeFilterText = m_ExcludeFilter.value;
            m_GroupByCallsiteValue = m_GroupByCallsite.value;
            m_SelectedMarkerIndex = m_MarkerListView.selectedIndex;

            // Serialize thread selection (HashSet is not serializable).
            // Empty array = "all threads" (no filtering active).
            if (m_SelectedThreads.Count > 0 && m_SelectedThreads.Count < m_AllThreadNames.Count)
            {
                m_SerializedSelectedThreads = new string[m_SelectedThreads.Count];
                int idx = 0;
                foreach (string t in m_SelectedThreads)
                    m_SerializedSelectedThreads[idx++] = t;
            }
            else
            {
                m_SerializedSelectedThreads = Array.Empty<string>();
            }
        }

        void OnSettingsChanged()
        {
            m_MarkerListView?.RefreshItems();
            if (m_Snapshot != null && m_Snapshot.HasData)
                PopulateTopOffendersUI();
        }

        /// <summary>
        /// After domain reload, serialized fields survive but UI is rebuilt.
        /// Rebuild groupings from the surviving raw data and restore the view.
        /// </summary>
        void TryRestoreAfterReload()
        {
            m_Snapshot.EnsureNonSerializedLists();
            if (!m_Snapshot.HasData) return;

            // Rebuild thread HashSet from serialized sorted list
            m_AllThreadNames.Clear();
            m_ThreadAllocCounts.Clear();
            for (int i = 0; i < m_Snapshot.SortedThreadNames.Count; i++)
                m_AllThreadNames.Add(m_Snapshot.SortedThreadNames[i]);

            // Restore thread selection from serialized array
            m_SelectedThreads.Clear();
            for (int i = 0; i < m_SerializedSelectedThreads.Length; i++)
            {
                if (m_AllThreadNames.Contains(m_SerializedSelectedThreads[i]))
                    m_SelectedThreads.Add(m_SerializedSelectedThreads[i]);
            }

            RebuildThreadAllocCounts();
            UpdateThreadButtonLabel();

            // Rebuild both groupings from raw data
            BuildGrouping(true, m_Snapshot.GroupsByFullCallstack);
            BuildGrouping(false, m_Snapshot.GroupsByTopFrame);
            ComputeSnapshotPerFrameBytes();

            // Restore frame store from snapshot if needed
            if (!m_FrameStore.HasFullFrameData && m_Snapshot.PerFrameBytes != null)
            {
                m_FrameStore.FullFrameStart = m_Snapshot.FrameStart;
                m_FrameStore.FullFrameEnd = m_Snapshot.FrameEnd;
                int count = m_Snapshot.PerFrameBytes.Length;
                m_FrameStore.FullFrameBytes = new long[count];
                Array.Copy(m_Snapshot.PerFrameBytes, m_FrameStore.FullFrameBytes, count);
            }

            m_ActiveGroups = m_GroupByCallsite.value
                ? m_Snapshot.GroupsByFullCallstack : m_Snapshot.GroupsByTopFrame;
            BuildThreadIndex(m_ActiveGroups);
            BuildTopOffenders();
            ApplyFilters();
            UpdateDataSummary();
            ShowNoDataState(m_ActiveGroups.Count == 0);

            // Restore selected marker (ApplyFilters defaults to index 0)
            if (m_SelectedMarkerIndex >= 0 && m_SelectedMarkerIndex < m_FilteredGroups.Count)
                m_MarkerListView.selectedIndex = m_SelectedMarkerIndex;

            RebuildGraph();

            // Restore frame range in UI (fields display 1-based)
            m_StartFrameField.value = GCAllocUtils.DisplayFrame(m_Snapshot.FrameStart);
            m_EndFrameField.value = GCAllocUtils.DisplayFrame(m_Snapshot.FrameEnd);
            UpdateFrameRangeInfo();

            m_SharedSB.Clear();
            m_SharedSB.Append("Restored: ");
            m_SharedSB.Append(m_Snapshot.TotalCount);
            m_SharedSB.Append(" allocs across frames ");
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(m_Snapshot.FrameStart));
            m_SharedSB.Append('–');
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(m_Snapshot.FrameEnd));
            m_StatusLabel.text = m_SharedSB.ToString();
            RestoreMarkerSortIndicator();
            m_SaveBtn?.SetEnabled(true);
            m_ExportBtn?.SetEnabled(true);

            if (m_IsLoadedSnapshot)
                m_LoadedSnapshotLabel.style.display = DisplayStyle.Flex;
        }

        // ═══════════════════════════════════════════════════
        //  TOOLBAR
        // ═══════════════════════════════════════════════════

        VisualElement BuildToolbar()
        {
            var bar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    paddingTop = 4, paddingBottom = 4,
                    paddingLeft = 8, paddingRight = 8,
                    borderBottomWidth = 1,
                    borderBottomColor = new Color(0.2f, 0.2f, 0.2f),
                    flexShrink = 0,
                    flexWrap = Wrap.Wrap
                }
            };

            var pullBtn = new Button(OnPullData) { text = "Pull Data", style = { marginRight = 4 } };
            bar.Add(pullBtn);

            var analyzeBtn = new Button(OnAnalyze) { text = "Analyze", style = { marginRight = 2 } };
            bar.Add(analyzeBtn);

            m_SaveBtn = new Button(OnSaveSnapshot) { text = "Save", style = { marginRight = 2 } };
            m_SaveBtn.SetEnabled(false);
            bar.Add(m_SaveBtn);

            m_LoadBtn = new Button(OnLoadSnapshot) { text = "Load", style = { marginRight = 2 } };
            bar.Add(m_LoadBtn);

            m_ExportBtn = new Button(ShowExportMenu) { text = "Export \u25be", style = { marginRight = 12 } };
            m_ExportBtn.SetEnabled(false);
            bar.Add(m_ExportBtn);

            bar.Add(MakeSeparator());

            bar.Add(new Label("Frames:")
            {
                style = { marginRight = 4, unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11 }
            });

            m_StartFrameField = new IntegerField { value = 0, style = { width = 60, marginRight = 2 } };
            m_StartFrameField.RegisterValueChangedCallback(OnFrameRangeChanged);
            bar.Add(m_StartFrameField);

            bar.Add(new Label("—") { style = { marginLeft = 2, marginRight = 2 } });

            m_EndFrameField = new IntegerField { value = 0, style = { width = 60, marginRight = 4 } };
            m_EndFrameField.RegisterValueChangedCallback(OnFrameRangeChanged);
            bar.Add(m_EndFrameField);

            m_FrameRangeInfo = new Label("")
            {
                style = { fontSize = 10, color = k_SubtleText, marginRight = 12 }
            };
            bar.Add(m_FrameRangeInfo);

            bar.Add(new VisualElement { style = { flexGrow = 1 } });

            m_LoadedSnapshotLabel = new Label("Loaded Snapshot — Profiler sync disabled")
            {
                style = { fontSize = 11, color = new Color(1f, 0.8f, 0.3f),
                    unityFontStyleAndWeight = FontStyle.Italic,
                    display = DisplayStyle.None, marginRight = 8 }
            };
            bar.Add(m_LoadedSnapshotLabel);

            var openProfilerBtn = new Button(OnOpenProfiler)
            {
                text = "Open Profiler",
                style = { marginLeft = 8 }
            };
            bar.Add(openProfilerBtn);

            return bar;
        }

        // Non-capturing callbacks for toolbar
        void OnFrameRangeChanged(ChangeEvent<int> evt) => UpdateFrameRangeInfo();
        void OnOpenProfiler() => EditorWindow.GetWindow<ProfilerWindow>();

        void OnSaveSnapshot()
        {
            if (!m_Snapshot.HasData)
            {
                m_StatusLabel.text = "No data to save. Run analysis first.";
                return;
            }

            m_SharedSB.Clear();
            m_SharedSB.Append("GCSnapshot_");
            m_SharedSB.Append(DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            string defaultName = m_SharedSB.ToString();

            string projectDir = Path.GetDirectoryName(Application.dataPath);
            string path = EditorUtility.SaveFilePanel("Save GC Alloc Snapshot", projectDir, defaultName, "json");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                string json = JsonUtility.ToJson(m_Snapshot, true);
                File.WriteAllText(path, json);

                m_SharedSB.Clear();
                m_SharedSB.Append("Saved snapshot to: ");
                m_SharedSB.Append(Path.GetFileName(path));
                m_StatusLabel.text = m_SharedSB.ToString();
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Concat("Failed to save GC snapshot: ", ex.Message));
                m_StatusLabel.text = "Failed to save snapshot. See console for details.";
            }
        }

        void OnLoadSnapshot()
        {
            string projectDir = Path.GetDirectoryName(Application.dataPath);
            string path = EditorUtility.OpenFilePanel("Load GC Alloc Snapshot", projectDir, "json");
            if (string.IsNullOrEmpty(path)) return;

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Concat("Failed to read snapshot file: ", ex.Message));
                m_StatusLabel.text = "Failed to read file. See console for details.";
                return;
            }

            AnalysisSnapshot loaded;
            try
            {
                loaded = JsonUtility.FromJson<AnalysisSnapshot>(json);
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Concat("Failed to parse snapshot JSON: ", ex.Message));
                m_StatusLabel.text = "Invalid snapshot file. See console for details.";
                return;
            }

            if (loaded == null || loaded.RawAllocations == null || loaded.RawAllocations.Count == 0)
            {
                m_StatusLabel.text = "Snapshot file contains no allocation data.";
                return;
            }

            // Replace current snapshot and rebuild state (mirrors TryRestoreAfterReload)
            m_Snapshot = loaded;
            m_Snapshot.EnsureNonSerializedLists();

            m_AllThreadNames.Clear();
            m_SelectedThreads.Clear();
            m_ThreadAllocCounts.Clear();
            for (int i = 0; i < m_Snapshot.SortedThreadNames.Count; i++)
                m_AllThreadNames.Add(m_Snapshot.SortedThreadNames[i]);
            RebuildThreadAllocCounts();
            UpdateThreadButtonLabel();

            BuildGrouping(true, m_Snapshot.GroupsByFullCallstack);
            BuildGrouping(false, m_Snapshot.GroupsByTopFrame);
            ComputeSnapshotPerFrameBytes();

            // Populate frame store from loaded snapshot
            m_FrameStore.FullFrameStart = m_Snapshot.FrameStart;
            m_FrameStore.FullFrameEnd = m_Snapshot.FrameEnd;
            if (m_Snapshot.PerFrameBytes != null)
            {
                int count = m_Snapshot.PerFrameBytes.Length;
                m_FrameStore.FullFrameBytes = new long[count];
                Array.Copy(m_Snapshot.PerFrameBytes, m_FrameStore.FullFrameBytes, count);
            }
            m_FrameStore.CacheAnalysis(m_Snapshot.RawAllocations, m_Snapshot.SortedThreadNames);

            m_ActiveGroups = m_GroupByCallsite.value
                ? m_Snapshot.GroupsByFullCallstack : m_Snapshot.GroupsByTopFrame;
            BuildThreadIndex(m_ActiveGroups);
            BuildTopOffenders();
            ApplyFilters();
            UpdateDataSummary();
            ShowNoDataState(m_ActiveGroups.Count == 0);
            RebuildGraph();

            m_StartFrameField.value = GCAllocUtils.DisplayFrame(m_Snapshot.FrameStart);
            m_EndFrameField.value = GCAllocUtils.DisplayFrame(m_Snapshot.FrameEnd);
            UpdateFrameRangeInfo();

            m_SaveBtn.SetEnabled(true);
            m_ExportBtn.SetEnabled(true);

            if (m_FilteredGroups.Count > 0)
                m_MarkerListView.selectedIndex = 0;

            m_SharedSB.Clear();
            m_SharedSB.Append("Loaded: ");
            m_SharedSB.Append(m_Snapshot.TotalCount);
            m_SharedSB.Append(" allocs across frames ");
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(m_Snapshot.FrameStart));
            m_SharedSB.Append('\u2013');
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(m_Snapshot.FrameEnd));
            m_SharedSB.Append(" from ");
            m_SharedSB.Append(Path.GetFileName(path));
            m_StatusLabel.text = m_SharedSB.ToString();
            m_IsLoadedSnapshot = true;
            m_LoadedSnapshotLabel.style.display = DisplayStyle.Flex;
        }

        void UpdateFrameRangeInfo()
        {
            int count = m_EndFrameField.value - m_StartFrameField.value + 1;
            m_FrameRangeInfo.text = count > 0 ? string.Concat("(", count.ToString(), " frames)") : "";
        }

        static VisualElement MakeSeparator()
        {
            return new VisualElement
            {
                style =
                {
                    width = 1, height = 18,
                    backgroundColor = new Color(0.3f, 0.3f, 0.3f),
                    marginLeft = 4, marginRight = 8
                }
            };
        }

        // ═══════════════════════════════════════════════════
        //  LEFT PANEL
        // ═══════════════════════════════════════════════════

        VisualElement BuildLeftPanel()
        {
            var left = new VisualElement { style = { flexGrow = 1, minWidth = 350 } };

            // Filters (collapsible)
            m_FiltersFoldout = MakeSectionFoldout("Filters");
            m_FiltersFoldout.style.marginLeft = 4;
            m_FiltersFoldout.style.marginRight = 4;
            m_FiltersFoldout.style.marginTop = 0;

            var filterRow1 = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, marginBottom = 4, flexWrap = Wrap.Wrap }
            };

            m_NameFilter = new TextField("Name:")
            {
                style = { minWidth = 150, flexGrow = 1, marginRight = 12 }
            };
            m_NameFilter.SetValueWithoutNotify(m_NameFilterText);
            m_NameFilter.RegisterValueChangedCallback(OnNameFilterChanged);
            filterRow1.Add(m_NameFilter);

            m_ExcludeFilter = new TextField("Exclude:")
            {
                style = { minWidth = 150, flexGrow = 1, marginRight = 12 }
            };
            m_ExcludeFilter.SetValueWithoutNotify(m_ExcludeFilterText);
            m_ExcludeFilter.RegisterValueChangedCallback(OnExcludeFilterChanged);
            filterRow1.Add(m_ExcludeFilter);

            var threadGroup = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center }
            };
            threadGroup.Add(new Label("Threads:") { style = { marginRight = 4, fontSize = 11 } });
            m_ThreadFilterBtn = new Button(ShowThreadFilterMenu)
            {
                text = "All Threads ▾",
                style = { minWidth = 120 }
            };
            threadGroup.Add(m_ThreadFilterBtn);
            filterRow1.Add(threadGroup);
            m_FiltersFoldout.Add(filterRow1);

            var filterRow2 = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            m_GroupByCallsite = new Toggle("Group By Full Callsite")
            {
                tooltip = "ON: group by full call stack.\nOFF: group by top frame only."
            };
            m_GroupByCallsite.SetValueWithoutNotify(m_GroupByCallsiteValue);
            m_GroupByCallsite.RegisterValueChangedCallback(OnGroupByChanged);
            filterRow2.Add(m_GroupByCallsite);

            var showAsmToggle = new Toggle("Show Assembly")
            {
                value = m_ShowAssembly,
                tooltip = "Show or hide the DLL/assembly prefix on method names.",
                style = { marginLeft = 12 }
            };
            showAsmToggle.RegisterValueChangedCallback(OnShowAssemblyChanged);
            filterRow2.Add(showAsmToggle);

            m_FiltersFoldout.Add(filterRow2);

            left.Add(m_FiltersFoldout);
            left.Add(BuildPerFrameGraph());

            // Marker list (multi-column, virtualized)
            m_MarkerListView = new MultiColumnListView
            {
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                fixedItemHeight = MARKER_ROW_HEIGHT,
                selectionType = SelectionType.Single,
                showBorder = true,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                sortingMode = ColumnSortingMode.Custom,
                style = { flexGrow = 1 }
            };

            m_MarkerListView.columns.Add(new Column { name = k_ColSite, title = "Allocation Site",
                stretchable = true, minWidth = 200, sortable = true,
                makeHeader = k_HeaderSite, makeCell = MakeMarkerSiteCell, bindCell = BindMarkerSite });
            m_MarkerListView.columns.Add(new Column { name = k_ColBytes, title = "Bytes",
                width = COL_BYTES, sortable = true, resizable = true,
                makeHeader = k_HeaderBytes, makeCell = MakeMarkerCellWithMenu, bindCell = BindMarkerBytes,
                unbindCell = UnbindMarkerStyledCell });
            m_MarkerListView.columns.Add(new Column { name = k_ColCount, title = "Count",
                width = COL_COUNT, sortable = true, resizable = true,
                makeHeader = k_HeaderCount, makeCell = MakeMarkerCellWithMenu, bindCell = BindMarkerCount });
            m_MarkerListView.columns.Add(new Column { name = k_ColAvg, title = "Avg",
                width = COL_AVG, sortable = true, resizable = true,
                makeHeader = k_HeaderAvg, makeCell = MakeMarkerCellWithMenu, bindCell = BindMarkerAvg });
            m_MarkerListView.columns.Add(new Column { name = k_ColPct, title = "%",
                width = COL_PCT, sortable = true, resizable = true,
                makeHeader = k_HeaderPct, makeCell = MakeMarkerCellWithMenu, bindCell = BindMarkerPct });
            m_MarkerListView.columns.Add(new Column { name = k_ColMedian, title = "Median",
                width = COL_MEDIAN, sortable = true, resizable = true,
                makeHeader = k_HeaderMedian, makeCell = MakeMarkerCellWithMenu, bindCell = BindMarkerMedian });
            m_MarkerListView.columns.Add(new Column { name = k_ColMean, title = "Mean",
                width = COL_MEAN, sortable = true, resizable = true,
                makeHeader = k_HeaderMean, makeCell = MakeMarkerCellWithMenu, bindCell = BindMarkerMean });
            m_MarkerListView.columns.Add(new Column { name = k_ColMin, title = "Min",
                width = COL_MIN, sortable = true, resizable = true,
                makeHeader = k_HeaderMin, makeCell = MakeMarkerCellWithMenu, bindCell = BindMarkerMin });
            m_MarkerListView.columns.Add(new Column { name = k_ColMax, title = "Max",
                width = COL_MAX, sortable = true, resizable = true,
                makeHeader = k_HeaderMax, makeCell = MakeMarkerCellWithMenu, bindCell = BindMarkerMax,
                unbindCell = UnbindMarkerStyledCell });
            m_MarkerListView.columns.Add(new Column { name = k_ColRange, title = "Range",
                width = COL_RANGE, sortable = true, resizable = true,
                makeHeader = k_HeaderRange, makeCell = MakeMarkerCellWithMenu, bindCell = BindMarkerRange });
            m_MarkerListView.columns.Add(new Column { name = k_ColFirst, title = "First",
                width = COL_FIRST, sortable = true, resizable = true,
                makeHeader = k_HeaderFirst, makeCell = MakeMarkerCellWithMenu, bindCell = BindMarkerFirst });

            m_MarkerListView.itemsSource = m_FilteredGroups;

            // Restore sort indicator before subscribing to avoid double-sort
            RestoreMarkerSortIndicator();
            m_MarkerListView.columnSortingChanged += OnMarkerColumnSortingChanged;
            m_MarkerListView.selectionChanged += OnMarkerSelectionChanged;

            left.Add(m_MarkerListView);

            return left;
        }

        // ═══════════════════════════════════════════════════
        //  PER-FRAME BAR GRAPH — delegated to PerFrameGraphController
        // ═══════════════════════════════════════════════════

        VisualElement BuildPerFrameGraph()
        {
            m_GraphController = new PerFrameGraphController(OnGraphFrameSelected, () => m_MarkerListView?.selectedIndex ?? -1);
            m_GraphController.OnDragCompleted += OnGraphDragCompleted;
            m_GraphController.OnResetRequested += OnGraphResetRequested;
            m_GraphController.SetData(m_FrameStore, m_Snapshot, m_FilteredGroups);
            m_GraphController.RestoreState(m_GraphState);
            return m_GraphController.Root;
        }

        void OnGraphDragCompleted(int startFrame, int endFrame)
        {
            if (m_FrameStore.HasCachedAnalysis)
            {
                // Use the frame buffer for precise filtering (handles both
                // contiguous frame-order and non-contiguous sorted selections)
                var buffer = m_GraphController?.SelectedFrameBuffer;
                if (buffer != null)
                    RebuildFromCacheWithBuffer(buffer, m_GraphController.SelectedFrameBaseFrame,
                        startFrame, endFrame);
                else
                    RebuildFromCache(startFrame, endFrame);

                // Clear visual selection (the selection "became" the analyzed range)
                m_GraphController?.ClearSelection();
            }
            else
            {
                // No cache yet — just update frame fields
                if (m_StartFrameField != null)
                    m_StartFrameField.SetValueWithoutNotify(GCAllocUtils.DisplayFrame(startFrame));
                if (m_EndFrameField != null)
                    m_EndFrameField.SetValueWithoutNotify(GCAllocUtils.DisplayFrame(endFrame));
                UpdateFrameRangeInfo();
            }
        }

        void OnGraphResetRequested()
        {
            if (!m_FrameStore.HasCachedAnalysis) return;
            m_GraphController?.ClearFrameSelection();
            RebuildFromCache(m_FrameStore.FullFrameStart, m_FrameStore.FullFrameEnd);
            m_GraphController?.ClearSelection();
        }

        void OnGraphFrameSelected(int frameIndex)
        {
            EnsureProfilerRef();
            if (m_ProfilerWindow != null)
            {
                try { m_ProfilerWindow.selectedFrameIndex = frameIndex; }
                catch (Exception e)
                {
                    Debug.LogWarning(string.Concat("[GC Alloc Analyzer] Graph click frame=", frameIndex.ToString(), ": ", e.Message));
                }
            }

            // Select the allocation site with the most bytes in this frame
            SelectTopAllocatorForFrame(frameIndex);
        }

        void SelectTopAllocatorForFrame(int frameIndex)
        {
            if (m_FilteredGroups == null || m_FilteredGroups.Count == 0) return;

            int bestIdx = -1;
            long bestBytes = 0;
            for (int g = 0; g < m_FilteredGroups.Count; g++)
            {
                var group = m_FilteredGroups[g];
                long groupBytes = 0;
                for (int a = 0; a < group.Allocations.Count; a++)
                {
                    if (group.Allocations[a].FrameIndex == frameIndex)
                        groupBytes += group.Allocations[a].Bytes;
                }
                if (groupBytes > bestBytes)
                {
                    bestBytes = groupBytes;
                    bestIdx = g;
                }
            }

            if (bestIdx >= 0 && bestIdx != m_MarkerListView.selectedIndex)
            {
                m_MarkerListView.selectedIndex = bestIdx;
                m_MarkerListView.ScrollToItem(bestIdx);
            }
        }

        void RebuildGraph()
        {
            m_GraphController?.SetData(m_FrameStore, m_Snapshot, m_FilteredGroups);
            m_GraphController?.RebuildGraph();
        }
        void UpdateGraphOverlay(CallsiteGroup group) => m_GraphController?.UpdateOverlay(group);
        void ClearGraphOverlay() => m_GraphController?.ClearOverlay();

        // Non-capturing filter callbacks
        void OnNameFilterChanged(ChangeEvent<string> evt) => ApplyFilters();
        void OnExcludeFilterChanged(ChangeEvent<string> evt) => ApplyFilters();
        void OnGroupByChanged(ChangeEvent<bool> evt) => SwapGroupingAndRefresh();

        void OnShowAssemblyChanged(ChangeEvent<bool> evt)
        {
            m_ShowAssembly = evt.newValue;
            if (!m_Snapshot.HasData) return;

            // Fast path: just swap display names on existing groups (no rebuild)
            SwapDisplayNames(m_Snapshot.GroupsByFullCallstack);
            SwapDisplayNames(m_Snapshot.GroupsByTopFrame);
            PopulateTopOffendersUI();
            m_MarkerListView.RefreshItems();

            // Update selected marker summary if anything is selected
            if (m_MarkerListView.selectedIndex >= 0 &&
                m_MarkerListView.selectedIndex < m_FilteredGroups.Count)
                UpdateMarkerSummary(m_FilteredGroups[m_MarkerListView.selectedIndex]);
        }

        void SwapDisplayNames(List<CallsiteGroup> groups)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (g.Allocations.Count > 0)
                {
                    var first = g.Allocations[0];
                    g.DisplayName = m_ShowAssembly
                        ? first.DisplayNameWithAssembly : first.DisplayName;
                }
            }
        }

        // ═══════════════════════════════════════════════════
        //  THREAD FILTER — GenericMenu (closures unavoidable in GenericMenu API,
        //  but only created on click, not per-frame)
        // ═══════════════════════════════════════════════════

        void ShowThreadFilterMenu()
        {
            var menu = new GenericMenu();
            bool allSelected = m_SelectedThreads.Count == 0 ||
                               m_SelectedThreads.Count == m_AllThreadNames.Count;

            menu.AddItem(new GUIContent("All Threads"), allSelected, OnThreadMenuAll);

            // Quick-select shortcuts for common threads
            for (int i = 0; i < m_Snapshot.SortedThreadNames.Count; i++)
            {
                string t = m_Snapshot.SortedThreadNames[i];
                if (t == k_MainThread || t == k_RenderThread)
                {
                    bool on = m_SelectedThreads.Count == 1 && m_SelectedThreads.Contains(t);
                    menu.AddItem(new GUIContent(string.Concat(t, " Only")), on, OnThreadMenuSolo, t);
                }
            }

            menu.AddSeparator("");

            for (int i = 0; i < m_Snapshot.SortedThreadNames.Count; i++)
            {
                string thread = m_Snapshot.SortedThreadNames[i];
                bool on = m_SelectedThreads.Count == 0 || m_SelectedThreads.Contains(thread);

                // Show allocation count next to thread name
                m_SharedSB.Clear();
                m_SharedSB.Append(thread);
                if (m_ThreadAllocCounts.TryGetValue(thread, out int count))
                {
                    m_SharedSB.Append("  (");
                    m_SharedSB.Append(count.ToString("N0"));
                    m_SharedSB.Append(" allocs)");
                }
                menu.AddItem(new GUIContent(m_SharedSB.ToString()), on, OnThreadMenuToggle, thread);
            }

            menu.ShowAsContext();
        }

        void OnThreadMenuAll()
        {
            m_SelectedThreads.Clear();
            UpdateThreadButtonLabel();
            ApplyFilters();
        }

        void OnThreadMenuSolo(object userData)
        {
            string thread = (string)userData;
            m_SelectedThreads.Clear();
            m_SelectedThreads.Add(thread);
            UpdateThreadButtonLabel();
            ApplyFilters();
        }

        void OnThreadMenuToggle(object userData)
        {
            string thread = (string)userData;

            if (m_SelectedThreads.Count == 0)
            {
                // Was "all" — switch to all-except-this
                m_SelectedThreads.Clear();
                foreach (string t in m_AllThreadNames)
                    if (t != thread) m_SelectedThreads.Add(t);
            }
            else if (m_SelectedThreads.Contains(thread))
            {
                m_SelectedThreads.Remove(thread);
                if (m_SelectedThreads.Count == 0)
                    m_SelectedThreads.Clear();  // revert to "all"
            }
            else
            {
                m_SelectedThreads.Add(thread);
                if (m_SelectedThreads.Count == m_AllThreadNames.Count)
                    m_SelectedThreads.Clear();  // all selected = "all"
            }

            UpdateThreadButtonLabel();
            ApplyFilters();
        }

        void UpdateThreadButtonLabel()
        {
            if (m_SelectedThreads.Count == 0 || m_SelectedThreads.Count == m_AllThreadNames.Count)
            {
                m_ThreadFilterBtn.text = "All Threads ▾";
            }
            else if (m_SelectedThreads.Count == 1)
            {
                // Get the single thread name without LINQ
                string single = null;
                foreach (string t in m_SelectedThreads) { single = t; break; }
                m_SharedSB.Clear();
                m_SharedSB.Append(single);
                m_SharedSB.Append(" ▾");
                m_ThreadFilterBtn.text = m_SharedSB.ToString();
            }
            else
            {
                m_SharedSB.Clear();
                m_SharedSB.Append(m_SelectedThreads.Count);
                m_SharedSB.Append(" Threads ▾");
                m_ThreadFilterBtn.text = m_SharedSB.ToString();
            }
        }

        /// <summary>
        /// Rebuilds m_ThreadAllocCounts from m_Snapshot.RawAllocations.
        /// Used after loading a snapshot or restoring after domain reload.
        /// </summary>
        void RebuildThreadAllocCounts()
        {
            m_ThreadAllocCounts.Clear();
            var allocs = m_Snapshot.RawAllocations;
            for (int i = 0; i < allocs.Count; i++)
            {
                string td = allocs[i].ThreadDisplayName;
                if (m_ThreadAllocCounts.TryGetValue(td, out int prev))
                    m_ThreadAllocCounts[td] = prev + 1;
                else
                    m_ThreadAllocCounts[td] = 1;
            }
        }

        // ═══════════════════════════════════════════════════
        //  CSV EXPORT — delegated to GCAllocExporter
        // ═══════════════════════════════════════════════════

        void ShowExportMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Marker Table CSV"), false, () => GCAllocExporter.ExportMarkerTableCSV(m_FilteredGroups));
            menu.AddItem(new GUIContent("Individual Allocations CSV"), false, () => GCAllocExporter.ExportAllocationsCSV(m_Snapshot));
            menu.ShowAsContext();
        }

        // ═══════════════════════════════════════════════════
        //  RIGHT PANEL
        // ═══════════════════════════════════════════════════

        VisualElement BuildRightPanel()
        {
            var right = new VisualElement
            {
                style = { paddingLeft = 8, paddingRight = 8, paddingTop = 4, minWidth = 280,
                    flexGrow = 1, overflow = Overflow.Hidden }
            };

            // Data Summary
            var summaryFoldout = MakeSectionFoldout("Data Summary");
            summaryFoldout.style.flexShrink = 0;
            summaryFoldout.style.marginTop = 0;

            m_FrameCountLabel = new Label("Frame Count: —") { style = { fontSize = 11, marginBottom = 1 } };
            m_FrameRangeLabel = new Label("Frame Range: —") { style = { fontSize = 11, marginBottom = 1 } };
            m_TotalGcLabel = new Label("Total GC: —") { style = { fontSize = 11, marginBottom = 1 } };
            m_TotalAllocsLabel = new Label("Total Allocs: —") { style = { fontSize = 11, marginBottom = 1 } };
            m_UniqueSitesLabel = new Label("Unique Sites: —") { style = { fontSize = 11, marginBottom = 1 } };

            summaryFoldout.Add(m_FrameCountLabel);
            summaryFoldout.Add(m_FrameRangeLabel);
            summaryFoldout.Add(m_TotalGcLabel);
            summaryFoldout.Add(m_TotalAllocsLabel);
            summaryFoldout.Add(m_UniqueSitesLabel);
            right.Add(summaryFoldout);

            // No-data placeholder
            m_NoDataLabel = new Label("Pull Data from the Profiler, set frame range, then click Analyze.")
            {
                style = { fontSize = 12, color = k_SubtleText, whiteSpace = WhiteSpace.Normal,
                    marginTop = 20, unityTextAlign = TextAnchor.MiddleCenter }
            };
            right.Add(m_NoDataLabel);

            // Top Offenders (populated after analysis)
            m_TopOffendersFoldout = MakeSectionFoldout("Top Offenders");
            m_TopOffendersFoldout.style.flexShrink = 0;
            m_TopOffendersFoldout.style.display = DisplayStyle.None;

            m_TopOffendersFoldout.Add(new Label("By Total Bytes")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11,
                    marginBottom = 2, marginTop = 2 }
            });
            m_TopByTotalContainer = new VisualElement { style = { marginBottom = 6 } };
            m_TopOffendersFoldout.Add(m_TopByTotalContainer);

            m_TopOffendersFoldout.Add(new Label("Largest Single Allocations")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11,
                    marginBottom = 2, marginTop = 2, borderTopWidth = 1,
                    borderTopColor = new Color(0.2f, 0.2f, 0.2f), paddingTop = 4 }
            });
            m_TopSpikesContainer = new VisualElement { style = { marginBottom = 2 } };
            m_TopOffendersFoldout.Add(m_TopSpikesContainer);

            right.Add(m_TopOffendersFoldout);

            // Selected Allocation Site (wraps marker detail + call stack + allocs)
            m_MarkerSummaryRoot = new VisualElement
            {
                style = { flexGrow = 1, flexShrink = 1, overflow = Overflow.Hidden }
            };
            m_MarkerSummaryRoot.style.display = DisplayStyle.None;

            var siteFoldout = MakeSectionFoldout("Selected Allocation Site");
            siteFoldout.style.flexGrow = 1;
            siteFoldout.style.flexShrink = 1;
            m_MarkerSummaryRoot.Add(siteFoldout);
            right.Add(m_MarkerSummaryRoot);

            m_MarkerNameLabel = new Label("—")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 13,
                    marginBottom = 2, whiteSpace = WhiteSpace.Normal }
            };
            siteFoldout.Add(m_MarkerNameLabel);

            m_MarkerSourceLabel = new Label("")
            {
                style = { fontSize = 11, color = k_SubtleText, marginBottom = 4 }
            };
            siteFoldout.Add(m_MarkerSourceLabel);

            m_MarkerStatsLabel = new Label("")
            {
                style = { fontSize = 11, marginBottom = 4, whiteSpace = WhiteSpace.Normal }
            };
            siteFoldout.Add(m_MarkerStatsLabel);

            // Call Stack
            siteFoldout.Add(new Label("Call Stack")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11,
                    marginBottom = 2, marginTop = 2, borderTopWidth = 1,
                    borderTopColor = new Color(0.2f, 0.2f, 0.2f), paddingTop = 4 }
            });

            m_CallStackScroll = new ScrollView(ScrollViewMode.Vertical)
            {
                style = { maxHeight = 220, marginBottom = 4, paddingLeft = 4,
                    borderLeftWidth = 2, borderLeftColor = new Color(0.3f, 0.3f, 0.3f) }
            };
            m_CallStackContainer = new VisualElement();
            m_CallStackScroll.Add(m_CallStackContainer);
            siteFoldout.Add(m_CallStackScroll);

            // Individual Allocations
            siteFoldout.Add(new Label("Individual Allocations")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11,
                    marginBottom = 2, marginTop = 2, borderTopWidth = 1,
                    borderTopColor = new Color(0.2f, 0.2f, 0.2f), paddingTop = 4 }
            });

            m_AllocListView = new MultiColumnListView
            {
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                fixedItemHeight = ALLOC_ROW_HEIGHT,
                selectionType = SelectionType.Single,
                showBorder = true,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                sortingMode = ColumnSortingMode.Custom,
                style = { flexGrow = 1, minHeight = 80 }
            };

            m_AllocListView.columns.Add(new Column { name = k_AllocColNum, title = "#",
                width = ALLOC_COL_NUM, sortable = false, resizable = false,
                makeHeader = k_HeaderAllocNum, makeCell = MakeAllocCellWithMenu, bindCell = BindAllocNum });
            m_AllocListView.columns.Add(new Column { name = k_AllocColSize, title = "Size",
                width = ALLOC_COL_SIZE, sortable = true, resizable = true,
                makeHeader = k_HeaderAllocSize, makeCell = MakeAllocCellWithMenu, bindCell = BindAllocSize });
            m_AllocListView.columns.Add(new Column { name = k_AllocColFrame, title = "Frame",
                width = ALLOC_COL_FRAME, sortable = true, resizable = true,
                makeHeader = k_HeaderAllocFrame, makeCell = MakeAllocCellWithMenu, bindCell = BindAllocFrame });
            m_AllocListView.columns.Add(new Column { name = k_AllocColThread, title = "Thread",
                stretchable = true, sortable = false, resizable = true,
                makeHeader = k_HeaderAllocThread, makeCell = MakeAllocThreadCell, bindCell = BindAllocThread });

            m_AllocListView.itemsSource = m_SelectedAllocations;
            m_AllocListView.columnSortingChanged += OnAllocColumnSortingChanged;
            m_AllocListView.selectionChanged += OnAllocSelectionChanged;
            siteFoldout.Add(m_AllocListView);

            return right;
        }

        void OnAllocColumnSortingChanged()
        {
            using var e = m_AllocListView.sortedColumns.GetEnumerator();
            if (!e.MoveNext()) return;
            var desc = e.Current;

            m_AllocSortCol = desc.columnName switch
            {
                k_AllocColSize  => AllocSortCol.Size,
                k_AllocColFrame => AllocSortCol.Frame,
                _               => m_AllocSortCol
            };
            m_AllocSortAsc = desc.direction == SortDirection.Ascending;

            SortAllocsAndRefresh();
        }

        void SortAllocsAndRefresh()
        {
            SortAllocsInPlace();
            m_AllocListView.RefreshItems();
        }

        static int CmpAllocSizeAsc(RawAllocation a, RawAllocation b) => a.Bytes.CompareTo(b.Bytes);
        static int CmpAllocSizeDesc(RawAllocation a, RawAllocation b) => b.Bytes.CompareTo(a.Bytes);
        static int CmpAllocFrameAsc(RawAllocation a, RawAllocation b) => a.FrameIndex.CompareTo(b.FrameIndex);
        static int CmpAllocFrameDesc(RawAllocation a, RawAllocation b) => b.FrameIndex.CompareTo(a.FrameIndex);

        void SortAllocsInPlace()
        {
            if (m_SelectedAllocations == null || m_SelectedAllocations.Count == 0) return;
            m_SelectedAllocations.Sort((m_AllocSortCol, m_AllocSortAsc) switch
            {
                (AllocSortCol.Size, true)   => CmpAllocSizeAsc,
                (AllocSortCol.Size, false)  => CmpAllocSizeDesc,
                (AllocSortCol.Frame, true)  => CmpAllocFrameAsc,
                (AllocSortCol.Frame, false) => CmpAllocFrameDesc,
                _                           => CmpAllocSizeDesc
            });
        }

        VisualElement BuildStatusBar()
        {
            var bar = new VisualElement
            {
                style = { paddingLeft = 8, paddingRight = 8, paddingTop = 3, paddingBottom = 3,
                    borderTopWidth = 1, borderTopColor = new Color(0.2f, 0.2f, 0.2f),
                    flexShrink = 0 }
            };
            m_StatusLabel = new Label("Ready — Pull Data from the Profiler to begin.")
            {
                style = { fontSize = 11, color = k_SubtleText }
            };
            bar.Add(m_StatusLabel);
            return bar;
        }

        // ═══════════════════════════════════════════════════
        //  TOOLBAR ACTIONS
        // ═══════════════════════════════════════════════════

        void OnPullData()
        {
            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;
            if (first < 0 || last < 0 || last < first)
            {
                m_StatusLabel.text = "No profiler data available. Capture or load data first.";
                return;
            }

            // Clear previous state
            m_FrameStore.Clear();
            m_Snapshot.RawAllocations.Clear();
            m_Snapshot.TotalBytes = 0;
            m_Snapshot.TotalCount = 0;
            m_Snapshot.PerFrameBytes = null;
            m_Snapshot.GroupsByFullCallstack?.Clear();
            m_Snapshot.GroupsByTopFrame?.Clear();
            m_FilteredGroups.Clear();
            m_ActiveGroups = null;
            ShowNoDataState(true);
            ClearGraphOverlay();

            int totalFrames = last - first + 1;
            long[] fullFrameBytes = new long[totalFrames];

            // Pre-scan the first frame to find thread indices that carry GC.Alloc.
            var pullGcThreads = new List<int>(16);
            for (int t = 0; t < 256; t++)
            {
                using var tv = ProfilerDriver.GetRawFrameDataView(first, t);
                if (!tv.valid) break;
                if (tv.GetMarkerId("GC.Alloc") != FrameDataView.invalidMarkerId)
                    pullGcThreads.Add(t);
            }

            try
            {
                for (int f = first; f <= last; f++)
                {
                    if ((f - first) % 50 == 0)
                    {
                        float progress = (float)(f - first) / totalFrames;
                        if (EditorUtility.DisplayCancelableProgressBar(
                            "Pulling Per-Frame GC Data",
                            string.Concat("Frame ", (f - first + 1).ToString(), " / ", totalFrames.ToString()),
                            progress))
                            break;
                    }

                    long frameTotal = 0;
                    for (int ti = 0; ti < pullGcThreads.Count; ti++)
                    {
                        using var raw = ProfilerDriver.GetRawFrameDataView(f, pullGcThreads[ti]);
                        if (!raw.valid) continue;

                        int gcAllocId = raw.GetMarkerId("GC.Alloc");
                        if (gcAllocId == FrameDataView.invalidMarkerId) continue;

                        for (int i = 0; i < raw.sampleCount; i++)
                        {
                            if (raw.GetSampleMarkerId(i) != gcAllocId) continue;
                            long bytes = raw.GetSampleMetadataAsLong(i, 0);
                            if (bytes > 0)
                                frameTotal += bytes;
                        }
                    }

                    fullFrameBytes[f - first] = frameTotal;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            m_FrameStore.FullFrameStart = first;
            m_FrameStore.FullFrameEnd = last;
            m_FrameStore.FullFrameBytes = fullFrameBytes;

            m_StartFrameField.value = GCAllocUtils.DisplayFrame(first);
            m_EndFrameField.value = GCAllocUtils.DisplayFrame(last);
            UpdateFrameRangeInfo();

            // Show graph with all bars dimmed (nothing analyzed yet)
            RebuildGraph();

            m_SharedSB.Clear();
            m_SharedSB.Append("Pulled: frames ");
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(first));
            m_SharedSB.Append('–');
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(last));
            m_SharedSB.Append(" (");
            m_SharedSB.Append(totalFrames);
            m_SharedSB.Append(" frames). Click Analyze for full call-stack analysis.");
            m_StatusLabel.text = m_SharedSB.ToString();
        }

        void OnAnalyze()
        {
            // Fields hold 1-based display values; convert to 0-based API indices
            int start = GCAllocUtils.ApiFrame(m_StartFrameField.value);
            int end = GCAllocUtils.ApiFrame(m_EndFrameField.value);
            if (end < start || start < 0)
            {
                m_StatusLabel.text = "Invalid frame range. Use Pull Data first.";
                return;
            }
            RunAnalysis(start, end);
        }

        // ═══════════════════════════════════════════════════
        //  DATA EXTRACTION
        // ═══════════════════════════════════════════════════

        void RunAnalysis(int startFrame, int endFrame)
        {
            m_SwTotal.Restart();

            m_IsLoadedSnapshot = false;
            m_LoadedSnapshotLabel.style.display = DisplayStyle.None;
            m_ScriptOpener.ClearCache();
            m_Snapshot.RawAllocations.Clear();
            m_AllThreadNames.Clear();
            m_SelectedThreads.Clear();
            m_ThreadAllocCounts.Clear();
            m_MethodInfoCache.Clear();
            m_CallStackCache.Clear();
            m_FormattedBytesCache.Clear();
            m_ResolveCallCount = 0;
            m_ResolveCacheHits = 0;
            m_StackCacheHits = 0;
            bool anyCallStacks = false;
            long totalBytes = 0;

            int totalFrames = endFrame - startFrame + 1;

            // Pre-compute formatted frame strings — at most totalFrames unique values
            m_FormattedFrameStrs = new string[totalFrames];
            for (int i = 0; i < totalFrames; i++)
                m_FormattedFrameStrs[i] = GCAllocUtils.DisplayFrame(startFrame + i).ToString();

            // ── Phase 1: Pre-scan thread indices ──
            m_SwThreadScan.Restart();
            int maxThreadIdx = 0;
            var gcThreadIndices = new List<int>(16);
            {
                using var probe = ProfilerDriver.GetRawFrameDataView(startFrame, 0);
                if (probe.valid)
                {
                    for (int t = 0; t < 256; t++)
                    {
                        using var tv = ProfilerDriver.GetRawFrameDataView(startFrame, t);
                        if (!tv.valid) break;
                        maxThreadIdx = t + 1;
                        if (tv.GetMarkerId("GC.Alloc") != FrameDataView.invalidMarkerId)
                            gcThreadIndices.Add(t);
                    }
                }
            }
            m_SwThreadScan.Stop();

            // ── Phase 2: Frame extraction ──
            m_SwExtraction.Restart();
            m_SwFrameDataView.Reset();
            m_SwSampleIteration.Reset();
            m_SwResolveMethod.Reset();
            m_SwObjectAlloc.Reset();

            try
            {
                for (int f = startFrame; f <= endFrame; f++)
                {
                    if ((f - startFrame) % 20 == 0)
                    {
                        float progress = (float)(f - startFrame) / totalFrames;
                        if (EditorUtility.DisplayCancelableProgressBar(
                            "Analyzing GC Allocations",
                            string.Concat("Frame ", (f - startFrame + 1).ToString(), " / ", totalFrames.ToString()),
                            progress))
                            break;
                    }

                    for (int ti = 0; ti < gcThreadIndices.Count; ti++)
                    {
                        int threadIdx = gcThreadIndices[ti];

                        m_SwFrameDataView.Start();
                        using var raw = ProfilerDriver.GetRawFrameDataView(f, threadIdx);
                        m_SwFrameDataView.Stop();

                        if (!raw.valid) continue;

                        int gcAllocId = raw.GetMarkerId("GC.Alloc");
                        if (gcAllocId == FrameDataView.invalidMarkerId) continue;

                        string threadName = raw.threadName;
                        string threadGroup = raw.threadGroupName;
                        ulong threadId = raw.threadId;

                        m_SharedSB.Clear();
                        if (!string.IsNullOrEmpty(threadGroup))
                        {
                            m_SharedSB.Append(threadGroup);
                            m_SharedSB.Append('.');
                        }
                        m_SharedSB.Append(threadName);
                        string threadDisplay = m_SharedSB.ToString();

                        m_DepthStack.Clear();

                        m_SwSampleIteration.Start();
                        for (int i = 0; i < raw.sampleCount; i++)
                        {
                            int markerId = raw.GetSampleMarkerId(i);
                            int childCount = raw.GetSampleChildrenCount(i);

                            while (m_DepthStack.Count > 0 &&
                                   m_DepthStack[m_DepthStack.Count - 1].Remaining <= 0)
                                m_DepthStack.RemoveAt(m_DepthStack.Count - 1);

                            if (m_DepthStack.Count > 0)
                                m_DepthStack[m_DepthStack.Count - 1].Remaining--;

                            if (markerId == gcAllocId)
                            {
                                long bytes = raw.GetSampleMetadataAsLong(i, 0);
                                if (bytes <= 0) goto pushDepth;
                                totalBytes += bytes;

                                // Get call stack addresses
                                m_AddrBuffer.Clear();
                                raw.GetSampleCallstack(i, m_AddrBuffer);

                                m_SwSampleIteration.Stop();

                                // ── Fast path: check call stack cache BEFORE resolving
                                // individual methods.  With ~373 unique stacks across
                                // 500K+ allocations, this skips 16M+ ResolveMethodInfo
                                // dictionary lookups on cache hits.
                                List<ResolvedFrame> resolvedFrames;
                                string fullCallstackKey;
                                string topFrameKey;
                                string displayName;
                                string displayNameWithAssembly;
                                string hierarchyPath;
                                string parentMethod;
                                bool stackCacheHit = false;

                                if (m_AddrBuffer.Count > 0)
                                {
                                    anyCallStacks = true;

                                    long addrHash = m_AddrBuffer.Count;
                                    for (int ah = 0; ah < m_AddrBuffer.Count; ah++)
                                        addrHash = addrHash * 6364136223846793005L + (long)m_AddrBuffer[ah];

                                    if (m_CallStackCache.TryGetValue(addrHash, out var cs))
                                    {
                                        // Cache hit — skip ALL method resolution
                                        resolvedFrames = cs.Frames;
                                        fullCallstackKey = cs.FullCallstackKey;
                                        topFrameKey = cs.TopFrameKey;
                                        displayName = cs.DisplayName;
                                        displayNameWithAssembly = cs.DisplayNameWithAssembly;
                                        hierarchyPath = "";
                                        parentMethod = "";
                                        m_StackCacheHits++;
                                        stackCacheHit = true;
                                    }
                                    else
                                    {
                                        // Cache miss — resolve each address
                                        m_SwResolveMethod.Start();
                                        m_FrameBuffer.Clear();
                                        for (int a = 0; a < m_AddrBuffer.Count; a++)
                                        {
                                            ulong addr = m_AddrBuffer[a];
                                            m_ResolveCallCount++;

                                            if (!m_MethodInfoCache.TryGetValue(addr, out var info))
                                            {
                                                info = raw.ResolveMethodInfo(addr);
                                                m_MethodInfoCache[addr] = info;
                                            }
                                            else
                                            {
                                                m_ResolveCacheHits++;
                                            }

                                            if (string.IsNullOrEmpty(info.methodName)) continue;
                                            m_FrameBuffer.Add(new ResolvedFrame
                                            {
                                                RawMethodName = info.methodName.Trim(),
                                                SourceFile = (info.sourceFileName ?? "").Trim(),
                                                SourceLine = (int)info.sourceFileLine
                                            });
                                        }
                                        m_SwResolveMethod.Stop();

                                        hierarchyPath = "";
                                        parentMethod = "";

                                        if (m_FrameBuffer.Count > 0)
                                        {
                                            resolvedFrames = new List<ResolvedFrame>(m_FrameBuffer.Count);
                                            for (int fc = 0; fc < m_FrameBuffer.Count; fc++)
                                                resolvedFrames.Add(m_FrameBuffer[fc]);

                                            fullCallstackKey = BuildNormalizedCallStackKey(resolvedFrames);
                                            topFrameKey = GCAllocUtils.NormalizeKeyPart(resolvedFrames[0]);
                                            displayName = GCAllocUtils.FormatTopFrame(resolvedFrames[0]);
                                            displayNameWithAssembly = GCAllocUtils.FormatTopFrameWithAssembly(resolvedFrames[0]);

                                            m_CallStackCache[addrHash] = new CachedCallStack
                                            {
                                                Frames = resolvedFrames,
                                                FullCallstackKey = fullCallstackKey,
                                                TopFrameKey = topFrameKey,
                                                DisplayName = displayName,
                                                DisplayNameWithAssembly = displayNameWithAssembly
                                            };
                                        }
                                        else
                                        {
                                            // Addresses present but none resolved — rare
                                            resolvedFrames = k_EmptyFrameList;
                                            fullCallstackKey = "";
                                            topFrameKey = "";
                                            displayName = "";
                                            displayNameWithAssembly = "";
                                        }
                                    }
                                }
                                else
                                {
                                    // No call stacks — resolve depth stack names lazily
                                    resolvedFrames = k_EmptyFrameList;
                                    fullCallstackKey = "";
                                    topFrameKey = "";
                                    for (int d = 0; d < m_DepthStack.Count; d++)
                                    {
                                        if (m_DepthStack[d].Name == null)
                                            m_DepthStack[d].Name = raw.GetMarkerName(m_DepthStack[d].MarkerId);
                                    }
                                    hierarchyPath = BuildHierarchyPath(m_DepthStack);
                                    parentMethod = m_DepthStack.Count > 0
                                        ? m_DepthStack[m_DepthStack.Count - 1].Name : "<root>";
                                    displayName = GCAllocUtils.StripAssembly(parentMethod);
                                    displayNameWithAssembly = GCAllocUtils.StripLeadingColons(parentMethod);
                                }

                                m_SwObjectAlloc.Start();

                                // Cache formatted byte strings
                                if (!m_FormattedBytesCache.TryGetValue(bytes, out string formattedBytes))
                                {
                                    formattedBytes = GCAllocUtils.FormatBytes(bytes);
                                    m_FormattedBytesCache[bytes] = formattedBytes;
                                }

                                var alloc = new RawAllocation
                                {
                                    Bytes = bytes,
                                    FrameIndex = f,
                                    RawSampleIndex = i,
                                    ThreadDisplayName = threadDisplay,
                                    ThreadName = threadName,
                                    ThreadGroupName = threadGroup,
                                    ThreadId = threadId,
                                    ThreadIndex = threadIdx,
                                    ParentMethod = parentMethod,
                                    HierarchyPath = hierarchyPath,
                                    ResolvedCallStack = resolvedFrames,
                                    FormattedBytes = formattedBytes,
                                    FormattedFrame = m_FormattedFrameStrs[f - startFrame],
                                    FullCallstackKey = fullCallstackKey,
                                    TopFrameKey = topFrameKey,
                                    DisplayName = displayName,
                                    DisplayNameWithAssembly = displayNameWithAssembly
                                };

                                m_Snapshot.RawAllocations.Add(alloc);

                                m_SwObjectAlloc.Stop();

                                // Track thread only when it has real allocations
                                m_AllThreadNames.Add(threadDisplay);
                                if (m_ThreadAllocCounts.TryGetValue(threadDisplay, out int prev))
                                    m_ThreadAllocCounts[threadDisplay] = prev + 1;
                                else
                                    m_ThreadAllocCounts[threadDisplay] = 1;

                                m_SwSampleIteration.Start();
                            }

                            pushDepth:
                            if (childCount > 0)
                                m_DepthStack.Add(new DepthEntry { MarkerId = markerId, Remaining = childCount });
                        }
                        m_SwSampleIteration.Stop();
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            m_SwExtraction.Stop();

            m_Snapshot.TotalBytes = totalBytes;
            m_Snapshot.TotalCount = m_Snapshot.RawAllocations.Count;
            m_Snapshot.FrameStart = startFrame;
            m_Snapshot.FrameEnd = endFrame;
            m_Snapshot.HadCallStacks = anyCallStacks;

            // If user clicked Analyze without Pull Data, populate FrameStore from snapshot
            if (!m_FrameStore.HasFullFrameData
                || m_FrameStore.FullFrameStart != startFrame
                || m_FrameStore.FullFrameEnd != endFrame)
            {
                m_FrameStore.FullFrameStart = startFrame;
                m_FrameStore.FullFrameEnd = endFrame;
                // Will be filled after ComputeSnapshotPerFrameBytes
            }

            // Build sorted thread names
            m_Snapshot.SortedThreadNames.Clear();
            foreach (string t in m_AllThreadNames)
                m_Snapshot.SortedThreadNames.Add(t);
            m_Snapshot.SortedThreadNames.Sort(StringComparer.Ordinal);

            UpdateThreadButtonLabel();

            // ── Phase 3: Grouping ──
            m_SwGrouping.Restart();
            BuildGrouping(true, m_Snapshot.GroupsByFullCallstack);
            BuildGrouping(false, m_Snapshot.GroupsByTopFrame);
            m_SwGrouping.Stop();

            // ── Phase 4: Per-frame stats ──
            m_SwPerFrame.Restart();
            ComputeSnapshotPerFrameBytes();
            m_SwPerFrame.Stop();

            // If FrameStore was created from snapshot, copy per-frame bytes
            if (m_FrameStore.FullFrameBytes == null
                || m_FrameStore.FullFrameBytes.Length != m_Snapshot.PerFrameBytes.Length)
            {
                int count = m_Snapshot.PerFrameBytes.Length;
                m_FrameStore.FullFrameBytes = new long[count];
                Array.Copy(m_Snapshot.PerFrameBytes, m_FrameStore.FullFrameBytes, count);
            }

            // Cache full extraction for instant sub-range analysis
            m_FrameStore.CacheAnalysis(m_Snapshot.RawAllocations, m_Snapshot.SortedThreadNames);

            // Set active based on current toggle
            m_ActiveGroups = m_GroupByCallsite.value ? m_Snapshot.GroupsByFullCallstack : m_Snapshot.GroupsByTopFrame;

            // Build thread index for active grouping
            BuildThreadIndex(m_ActiveGroups);

            // Build top offenders
            BuildTopOffenders();

            ApplyFilters();
            UpdateDataSummary();
            ShowNoDataState(m_ActiveGroups.Count == 0);
            RebuildGraph();
            m_SaveBtn?.SetEnabled(m_Snapshot.HasData);
            m_ExportBtn?.SetEnabled(m_Snapshot.HasData);

            m_SwTotal.Stop();

            // ── Timing report ──
            long resolveMisses = m_ResolveCallCount - m_ResolveCacheHits;
            long stackCacheMisses = m_Snapshot.TotalCount - m_StackCacheHits;
            Debug.Log(string.Concat(
                "[GCAllocAnalyzer] Analysis complete in ", m_SwTotal.ElapsedMilliseconds.ToString("N0"), "ms\n",
                "  Thread pre-scan:     ", m_SwThreadScan.ElapsedMilliseconds.ToString("N0"), "ms\n",
                "  Frame extraction: ", m_SwExtraction.ElapsedMilliseconds.ToString("N0"), "ms",
                "  (", totalFrames.ToString("N0"), " frames, ", gcThreadIndices.Count.ToString(), " GC threads)\n",
                "    GetRawFrameDataView:  ", m_SwFrameDataView.ElapsedMilliseconds.ToString("N0"), "ms\n",
                "    Sample iteration:     ", m_SwSampleIteration.ElapsedMilliseconds.ToString("N0"), "ms\n",
                "    ResolveMethodInfo:    ", m_SwResolveMethod.ElapsedMilliseconds.ToString("N0"), "ms",
                "  (calls: ", m_ResolveCallCount.ToString("N0"),
                " | cached: ", m_ResolveCacheHits.ToString("N0"),
                " | miss: ", resolveMisses.ToString("N0"), ")\n",
                "    Object allocation:    ", m_SwObjectAlloc.ElapsedMilliseconds.ToString("N0"), "ms",
                "  (stacks: ", m_CallStackCache.Count.ToString("N0"), " unique",
                " | deduped: ", m_StackCacheHits.ToString("N0"),
                " | bytes formats: ", m_FormattedBytesCache.Count.ToString("N0"), ")\n",
                "  BuildGrouping:        ", m_SwGrouping.ElapsedMilliseconds.ToString("N0"), "ms\n",
                "  ComputePerFrame:      ", m_SwPerFrame.ElapsedMilliseconds.ToString("N0"), "ms\n",
                "  Total allocations: ", m_Snapshot.TotalCount.ToString("N0"),
                " | Unique addresses: ", m_MethodInfoCache.Count.ToString("N0")));

            // Status
            m_SharedSB.Clear();
            m_SharedSB.Append("Analyzed ");
            m_SharedSB.Append(totalFrames);
            m_SharedSB.Append(" frames (");
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(startFrame));
            m_SharedSB.Append('–');
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(endFrame));
            m_SharedSB.Append(") in ");
            m_SharedSB.Append(m_SwTotal.ElapsedMilliseconds.ToString("N0"));
            m_SharedSB.Append("ms | ");
            m_SharedSB.Append(GCAllocUtils.FormatBytes(totalBytes));
            m_SharedSB.Append(", ");
            m_SharedSB.Append(m_Snapshot.TotalCount);
            m_SharedSB.Append(" allocs, ");
            m_SharedSB.Append(m_ActiveGroups.Count);
            m_SharedSB.Append(" sites | ");
            m_SharedSB.Append(anyCallStacks ? "Call Stacks: Enabled" : "⚠ Call Stacks: NOT detected");
            m_StatusLabel.text = m_SharedSB.ToString();

            if (m_FilteredGroups.Count > 0)
                m_MarkerListView.selectedIndex = 0;
        }

        /// <summary>
        /// Rebuild the analysis view from cached data for a sub-range.
        /// Filters CachedRawAllocations by frame range and rebuilds all
        /// groupings, stats, and UI. No Profiler access — near-instant.
        /// </summary>
        void RebuildFromCache(int startFrame, int endFrame)
        {
            if (!m_FrameStore.HasCachedAnalysis)
            {
                m_StatusLabel.text = "No cached analysis. Run Analyze first.";
                return;
            }

            m_Snapshot.RawAllocations.Clear();
            m_SelectedThreads.Clear();
            m_ThreadAllocCounts.Clear();
            long totalBytes = 0;
            bool anyCallStacks = false;

            // Filter cached allocations by frame range
            var cached = m_FrameStore.CachedRawAllocations;
            for (int i = 0; i < cached.Count; i++)
            {
                var alloc = cached[i];
                if (alloc.FrameIndex < startFrame || alloc.FrameIndex > endFrame)
                    continue;

                m_Snapshot.RawAllocations.Add(alloc);
                totalBytes += alloc.Bytes;
                if (alloc.ResolvedCallStack != null && alloc.ResolvedCallStack.Count > 0)
                    anyCallStacks = true;

                // Track per-thread counts for the sub-range
                string td = alloc.ThreadDisplayName;
                if (m_ThreadAllocCounts.TryGetValue(td, out int prev))
                    m_ThreadAllocCounts[td] = prev + 1;
                else
                    m_ThreadAllocCounts[td] = 1;
            }

            // Restore full thread set from cache (not just threads in the sub-range)
            m_AllThreadNames.Clear();
            var cachedThreads = m_FrameStore.CachedSortedThreadNames;
            if (cachedThreads != null)
            {
                for (int i = 0; i < cachedThreads.Count; i++)
                    m_AllThreadNames.Add(cachedThreads[i]);
            }

            m_Snapshot.TotalBytes = totalBytes;
            m_Snapshot.TotalCount = m_Snapshot.RawAllocations.Count;
            m_Snapshot.FrameStart = startFrame;
            m_Snapshot.FrameEnd = endFrame;
            m_Snapshot.HadCallStacks = anyCallStacks;

            // Rebuild thread names
            m_Snapshot.SortedThreadNames.Clear();
            foreach (string t in m_AllThreadNames)
                m_Snapshot.SortedThreadNames.Add(t);
            m_Snapshot.SortedThreadNames.Sort(StringComparer.Ordinal);
            UpdateThreadButtonLabel();

            // Rebuild groupings and stats
            BuildGrouping(true, m_Snapshot.GroupsByFullCallstack);
            BuildGrouping(false, m_Snapshot.GroupsByTopFrame);
            ComputeSnapshotPerFrameBytes();

            m_ActiveGroups = m_GroupByCallsite.value
                ? m_Snapshot.GroupsByFullCallstack : m_Snapshot.GroupsByTopFrame;
            BuildThreadIndex(m_ActiveGroups);
            BuildTopOffenders();
            ApplyFilters();
            UpdateDataSummary();
            ShowNoDataState(m_ActiveGroups.Count == 0);
            RebuildGraph();
            m_SaveBtn?.SetEnabled(m_Snapshot.HasData);
            m_ExportBtn?.SetEnabled(m_Snapshot.HasData);

            // Update frame range fields
            m_StartFrameField.SetValueWithoutNotify(GCAllocUtils.DisplayFrame(startFrame));
            m_EndFrameField.SetValueWithoutNotify(GCAllocUtils.DisplayFrame(endFrame));
            UpdateFrameRangeInfo();

            // Status
            int totalFrames = endFrame - startFrame + 1;
            m_SharedSB.Clear();
            m_SharedSB.Append("Sub-range: ");
            m_SharedSB.Append(totalFrames);
            m_SharedSB.Append(" frames (");
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(startFrame));
            m_SharedSB.Append('–');
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(endFrame));
            m_SharedSB.Append(") | ");
            m_SharedSB.Append(GCAllocUtils.FormatBytes(totalBytes));
            m_SharedSB.Append(", ");
            m_SharedSB.Append(m_Snapshot.TotalCount);
            m_SharedSB.Append(" allocs (from cache)");
            m_StatusLabel.text = m_SharedSB.ToString();

            if (m_FilteredGroups.Count > 0)
                m_MarkerListView.selectedIndex = 0;
        }

        /// <summary>
        /// Rebuild analysis from cached data using a boolean frame buffer for filtering.
        /// Handles both contiguous (frame-order) and non-contiguous (sorted) selections.
        /// </summary>
        void RebuildFromCacheWithBuffer(bool[] frameBuffer, int baseFrame, int startFrame, int endFrame)
        {
            if (!m_FrameStore.HasCachedAnalysis)
            {
                m_StatusLabel.text = "No cached analysis. Run Analyze first.";
                return;
            }

            m_Snapshot.RawAllocations.Clear();
            m_SelectedThreads.Clear();
            m_ThreadAllocCounts.Clear();
            long totalBytes = 0;
            bool anyCallStacks = false;
            int selectedFrameCount = 0;

            // Count selected frames
            int bufferLen = frameBuffer.Length;
            for (int i = 0; i < bufferLen; i++)
            {
                if (frameBuffer[i]) selectedFrameCount++;
            }

            // Filter cached allocations by frame buffer membership
            var cached = m_FrameStore.CachedRawAllocations;
            for (int i = 0; i < cached.Count; i++)
            {
                var alloc = cached[i];
                int idx = alloc.FrameIndex - baseFrame;
                if (idx < 0 || idx >= bufferLen || !frameBuffer[idx])
                    continue;

                m_Snapshot.RawAllocations.Add(alloc);
                totalBytes += alloc.Bytes;
                if (alloc.ResolvedCallStack != null && alloc.ResolvedCallStack.Count > 0)
                    anyCallStacks = true;

                // Track per-thread counts
                string td = alloc.ThreadDisplayName;
                if (m_ThreadAllocCounts.TryGetValue(td, out int prev))
                    m_ThreadAllocCounts[td] = prev + 1;
                else
                    m_ThreadAllocCounts[td] = 1;
            }

            // Restore full thread set from cache
            m_AllThreadNames.Clear();
            var cachedThreads = m_FrameStore.CachedSortedThreadNames;
            if (cachedThreads != null)
            {
                for (int i = 0; i < cachedThreads.Count; i++)
                    m_AllThreadNames.Add(cachedThreads[i]);
            }

            m_Snapshot.TotalBytes = totalBytes;
            m_Snapshot.TotalCount = m_Snapshot.RawAllocations.Count;
            m_Snapshot.FrameStart = startFrame;
            m_Snapshot.FrameEnd = endFrame;
            m_Snapshot.HadCallStacks = anyCallStacks;

            // Rebuild thread names
            m_Snapshot.SortedThreadNames.Clear();
            foreach (string t in m_AllThreadNames)
                m_Snapshot.SortedThreadNames.Add(t);
            m_Snapshot.SortedThreadNames.Sort(StringComparer.Ordinal);
            UpdateThreadButtonLabel();

            // Rebuild groupings and stats
            BuildGrouping(true, m_Snapshot.GroupsByFullCallstack);
            BuildGrouping(false, m_Snapshot.GroupsByTopFrame);
            ComputeSnapshotPerFrameBytes();

            m_ActiveGroups = m_GroupByCallsite.value
                ? m_Snapshot.GroupsByFullCallstack : m_Snapshot.GroupsByTopFrame;
            BuildThreadIndex(m_ActiveGroups);
            BuildTopOffenders();
            ApplyFilters();
            UpdateDataSummary();
            ShowNoDataState(m_ActiveGroups.Count == 0);
            RebuildGraph();
            m_SaveBtn?.SetEnabled(m_Snapshot.HasData);
            m_ExportBtn?.SetEnabled(m_Snapshot.HasData);

            // Update frame range fields
            m_StartFrameField.SetValueWithoutNotify(GCAllocUtils.DisplayFrame(startFrame));
            m_EndFrameField.SetValueWithoutNotify(GCAllocUtils.DisplayFrame(endFrame));
            UpdateFrameRangeInfo();

            // Status
            int totalFrames = endFrame - startFrame + 1;
            m_SharedSB.Clear();
            if (selectedFrameCount < totalFrames)
            {
                // Non-contiguous selection (sorted mode)
                m_SharedSB.Append("Selected ");
                m_SharedSB.Append(selectedFrameCount);
                m_SharedSB.Append(" frames (non-contiguous) | ");
            }
            else
            {
                // Contiguous selection (frame-order mode)
                m_SharedSB.Append("Sub-range: ");
                m_SharedSB.Append(totalFrames);
                m_SharedSB.Append(" frames (");
                m_SharedSB.Append(GCAllocUtils.DisplayFrame(startFrame));
                m_SharedSB.Append('\u2013');
                m_SharedSB.Append(GCAllocUtils.DisplayFrame(endFrame));
                m_SharedSB.Append(") | ");
            }
            m_SharedSB.Append(GCAllocUtils.FormatBytes(totalBytes));
            m_SharedSB.Append(", ");
            m_SharedSB.Append(m_Snapshot.TotalCount);
            m_SharedSB.Append(" allocs (from cache)");
            m_StatusLabel.text = m_SharedSB.ToString();

            if (m_FilteredGroups.Count > 0)
                m_MarkerListView.selectedIndex = 0;
        }

        // ═══════════════════════════════════════════════════
        //  GROUPING — builds into target list, no LINQ
        // ═══════════════════════════════════════════════════

        void BuildGrouping(bool byFullCallstack, List<CallsiteGroup> target)
        {
            target.Clear();
            m_GroupingDict.Clear();

            long total = m_Snapshot.TotalBytes > 0 ? m_Snapshot.TotalBytes : 1;

            for (int i = 0; i < m_Snapshot.RawAllocations.Count; i++)
            {
                var alloc = m_Snapshot.RawAllocations[i];
                string key;

                if (alloc.ResolvedCallStack.Count > 0)
                    key = byFullCallstack ? alloc.FullCallstackKey : alloc.TopFrameKey;
                else
                    key = string.Concat("no_cs|", alloc.ParentMethod);

                if (!m_GroupingDict.TryGetValue(key, out var g))
                {
                    g = new CallsiteGroup
                    {
                        Key = key,
                        DisplayName = m_ShowAssembly ? alloc.DisplayNameWithAssembly : alloc.DisplayName,
                        ResolvedCallStack = alloc.ResolvedCallStack.Count > 0 ? alloc.ResolvedCallStack : null,
                        Allocations = new List<RawAllocation>(16)
                    };
                    m_GroupingDict[key] = g;
                    target.Add(g);
                }

                g.TotalBytes += alloc.Bytes;
                g.Count++;
                g.Allocations.Add(alloc);
            }

            // Pre-compute display strings and per-frame stats for each group
            for (int i = 0; i < target.Count; i++)
            {
                var g = target[i];
                g.Percentage = (float)g.TotalBytes / total * 100f;
                g.FormattedBytes = GCAllocUtils.FormatBytes(g.TotalBytes);
                g.FormattedCount = string.Concat(g.Count.ToString(), "x");
                g.FormattedAvg = GCAllocUtils.FormatBytes(g.TotalBytes / Math.Max(1, g.Count));

                m_SharedSB.Clear();
                m_SharedSB.Append(g.Percentage.ToString("F1"));
                m_SharedSB.Append('%');
                g.FormattedPct = m_SharedSB.ToString();

                ComputePerFrameStats(g, m_Snapshot.FrameStart, m_Snapshot.FrameEnd);
                if (g.MaxBytesPerFrame > 0)
                {
                    g.FormattedMedian = GCAllocUtils.FormatBytes(g.MedianBytesPerFrame);
                    g.FormattedMin = GCAllocUtils.FormatBytes(g.MinBytesPerFrame);
                    g.FormattedMax = GCAllocUtils.FormatBytes(g.MaxBytesPerFrame);
                    g.FormattedMean = GCAllocUtils.FormatBytes((long)g.MeanBytesPerFrame);
                    g.RangeBytesPerFrame = g.MaxBytesPerFrame - g.MinBytesPerFrame;
                    g.FormattedRange = GCAllocUtils.FormatBytes(g.RangeBytesPerFrame);
                    g.FormattedFirst = GCAllocUtils.DisplayFrame(g.FirstFrame).ToString();
                    if (g.TopWorstFrameIndices != null)
                    {
                        g.FormattedTopWorst = new string[g.TopWorstFrameIndices.Length];
                        for (int tw = 0; tw < g.TopWorstFrameIndices.Length; tw++)
                        {
                            m_SharedSB.Clear();
                            m_SharedSB.Append("frame ");
                            m_SharedSB.Append(GCAllocUtils.DisplayFrame(g.TopWorstFrameIndices[tw]).ToString());
                            m_SharedSB.Append(" \u2014 ");
                            m_SharedSB.Append(GCAllocUtils.FormatBytes(g.TopWorstFrameBytes[tw]));
                            g.FormattedTopWorst[tw] = m_SharedSB.ToString();
                        }
                    }
                }
                else
                {
                    g.FormattedMedian = g.FormattedMin = g.FormattedMax = g.FormattedMean = "—";
                    g.FormattedRange = "—";
                    g.FormattedFirst = "—";
                    g.FormattedTopWorst = null;
                }
            }
        }

        void BuildThreadIndex(List<CallsiteGroup> groups)
        {
            // Clear existing sets for reuse instead of allocating new ones
            foreach (var kv in m_GroupThreadIndex)
                kv.Value.Clear();

            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (!m_GroupThreadIndex.TryGetValue(g.Key, out var threads))
                {
                    threads = new HashSet<string>();
                    m_GroupThreadIndex[g.Key] = threads;
                }
                for (int j = 0; j < g.Allocations.Count; j++)
                    threads.Add(g.Allocations[j].ThreadDisplayName);
            }
        }

        void SwapGroupingAndRefresh()
        {
            if (!m_Snapshot.HasData) return;

            m_ActiveGroups = m_GroupByCallsite.value ? m_Snapshot.GroupsByFullCallstack : m_Snapshot.GroupsByTopFrame;
            BuildThreadIndex(m_ActiveGroups);
            ApplyFilters();
        }

        // ═══════════════════════════════════════════════════
        //  PER-FRAME STATISTICS
        // ═══════════════════════════════════════════════════

        void EnsurePerFrameBuffer(int frameCount)
        {
            if (m_PerFrameBuffer == null || m_PerFrameBuffer.Length < frameCount)
                m_PerFrameBuffer = new long[frameCount];
            else
                Array.Clear(m_PerFrameBuffer, 0, frameCount);
        }

        void ComputePerFrameStats(CallsiteGroup group, int frameStart, int frameEnd)
        {
            int frameCount = frameEnd - frameStart + 1;
            if (frameCount <= 0 || group.Allocations.Count == 0) return;

            // Reuse class-level buffer to avoid per-group allocations
            EnsurePerFrameBuffer(frameCount);
            int firstFrame = int.MaxValue;

            for (int i = 0; i < group.Allocations.Count; i++)
            {
                var alloc = group.Allocations[i];
                int idx = alloc.FrameIndex - frameStart;
                if (idx >= 0 && idx < frameCount)
                    m_PerFrameBuffer[idx] += alloc.Bytes;
                if (alloc.FrameIndex < firstFrame)
                    firstFrame = alloc.FrameIndex;
            }

            group.FirstFrame = firstFrame;

            // Find min/max among frames that actually had allocations.
            // Min/Max/Median only consider frames with allocations.
            long min = long.MaxValue;
            long max = long.MinValue;
            int minFrame = frameStart;
            int maxFrame = frameStart;
            long sum = 0;
            int framesWithAllocs = 0;

            for (int i = 0; i < frameCount; i++)
            {
                long val = m_PerFrameBuffer[i];
                if (val > 0)
                {
                    framesWithAllocs++;
                    sum += val;
                    if (val < min) { min = val; minFrame = frameStart + i; }
                    if (val > max) { max = val; maxFrame = frameStart + i; }
                }
            }

            // Top 3 worst frames — extract from buffer before we pack it for median
            long top1 = 0, top2 = 0, top3 = 0;
            int top1f = -1, top2f = -1, top3f = -1;
            for (int i = 0; i < frameCount; i++)
            {
                long val = m_PerFrameBuffer[i];
                if (val > top1)
                {
                    top3 = top2; top3f = top2f;
                    top2 = top1; top2f = top1f;
                    top1 = val;  top1f = frameStart + i;
                }
                else if (val > top2)
                {
                    top3 = top2; top3f = top2f;
                    top2 = val;  top2f = frameStart + i;
                }
                else if (val > top3)
                {
                    top3 = val;  top3f = frameStart + i;
                }
            }

            int topCount = top1f >= 0 ? (top2f >= 0 ? (top3f >= 0 ? 3 : 2) : 1) : 0;
            if (topCount > 0)
            {
                group.TopWorstFrameIndices = new int[topCount];
                group.TopWorstFrameBytes = new long[topCount];
                if (topCount >= 1) { group.TopWorstFrameIndices[0] = top1f; group.TopWorstFrameBytes[0] = top1; }
                if (topCount >= 2) { group.TopWorstFrameIndices[1] = top2f; group.TopWorstFrameBytes[1] = top2; }
                if (topCount >= 3) { group.TopWorstFrameIndices[2] = top3f; group.TopWorstFrameBytes[2] = top3; }
            }

            if (framesWithAllocs == 0) return;

            group.MinBytesPerFrame = min;
            group.MaxBytesPerFrame = max;
            group.MinFrame = minFrame;
            group.MaxFrame = maxFrame;

            // Mean uses total frameCount (including zero-alloc frames) so it reflects
            // the amortized per-frame cost across the full analyzed range.
            group.MeanBytesPerFrame = (double)sum / frameCount;

            // Median: pack non-zero values to front of buffer, sort that region
            int ni = 0;
            for (int i = 0; i < frameCount; i++)
            {
                if (m_PerFrameBuffer[i] > 0)
                    m_PerFrameBuffer[ni++] = m_PerFrameBuffer[i];
            }
            Array.Sort(m_PerFrameBuffer, 0, framesWithAllocs);

            // Integer division is intentional — byte counts are discrete
            if (framesWithAllocs % 2 == 1)
                group.MedianBytesPerFrame = m_PerFrameBuffer[framesWithAllocs / 2];
            else
                group.MedianBytesPerFrame = (m_PerFrameBuffer[framesWithAllocs / 2 - 1] + m_PerFrameBuffer[framesWithAllocs / 2]) / 2;
        }

        void ComputeSnapshotPerFrameBytes()
        {
            int frameCount = m_Snapshot.FrameEnd - m_Snapshot.FrameStart + 1;
            if (frameCount <= 0) { m_Snapshot.PerFrameBytes = null; return; }

            if (m_Snapshot.PerFrameBytes == null || m_Snapshot.PerFrameBytes.Length < frameCount)
                m_Snapshot.PerFrameBytes = new long[frameCount];
            else
                Array.Clear(m_Snapshot.PerFrameBytes, 0, frameCount);

            for (int i = 0; i < m_Snapshot.RawAllocations.Count; i++)
            {
                var alloc = m_Snapshot.RawAllocations[i];
                int idx = alloc.FrameIndex - m_Snapshot.FrameStart;
                if (idx >= 0 && idx < frameCount)
                    m_Snapshot.PerFrameBytes[idx] += alloc.Bytes;
            }
        }

        // ═══════════════════════════════════════════════════
        //  FILTERING & SORTING — no LINQ, reuse lists
        // ═══════════════════════════════════════════════════

        void ApplyFilters()
        {
            if (m_ActiveGroups == null) return;

            string nameFilter = m_NameFilter != null ? m_NameFilter.value : "";
            string excludeFilter = m_ExcludeFilter != null ? m_ExcludeFilter.value : "";
            bool allThreads = m_SelectedThreads.Count == 0;

            m_FilteredGroups.Clear();
            for (int i = 0; i < m_ActiveGroups.Count; i++)
            {
                var g = m_ActiveGroups[i];

                // Name filter
                if (nameFilter.Length > 0 &&
                    g.DisplayName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // Exclude filter
                if (excludeFilter.Length > 0 &&
                    g.DisplayName.IndexOf(excludeFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                // Thread filter
                if (!allThreads)
                {
                    if (!PassesThreadFilter(g))
                        continue;
                }

                m_FilteredGroups.Add(g);
            }

            SortInPlace();
            RefreshMarkerListView();

            if (m_FilteredGroups.Count > 0)
                m_MarkerListView.selectedIndex = 0;
            else
                ClearMarkerSummary();
        }

        bool PassesThreadFilter(CallsiteGroup g)
        {
            if (!m_GroupThreadIndex.TryGetValue(g.Key, out var threads))
                return false;
            foreach (string t in m_SelectedThreads)
                if (threads.Contains(t)) return true;
            return false;
        }

        // Static sort comparisons — pre-allocated to avoid closure allocations on every sort
        static int CmpBytesAsc(CallsiteGroup a, CallsiteGroup b) => a.TotalBytes.CompareTo(b.TotalBytes);
        static int CmpBytesDesc(CallsiteGroup a, CallsiteGroup b) => b.TotalBytes.CompareTo(a.TotalBytes);
        static int CmpCountAsc(CallsiteGroup a, CallsiteGroup b) => a.Count.CompareTo(b.Count);
        static int CmpCountDesc(CallsiteGroup a, CallsiteGroup b) => b.Count.CompareTo(a.Count);
        static int CmpAvgAsc(CallsiteGroup a, CallsiteGroup b)
        { long aa = a.TotalBytes / Math.Max(1, a.Count), bb = b.TotalBytes / Math.Max(1, b.Count); return aa.CompareTo(bb); }
        static int CmpAvgDesc(CallsiteGroup a, CallsiteGroup b)
        { long aa = a.TotalBytes / Math.Max(1, a.Count), bb = b.TotalBytes / Math.Max(1, b.Count); return bb.CompareTo(aa); }
        static int CmpPctAsc(CallsiteGroup a, CallsiteGroup b) => a.Percentage.CompareTo(b.Percentage);
        static int CmpPctDesc(CallsiteGroup a, CallsiteGroup b) => b.Percentage.CompareTo(a.Percentage);
        static int CmpMedianAsc(CallsiteGroup a, CallsiteGroup b) => a.MedianBytesPerFrame.CompareTo(b.MedianBytesPerFrame);
        static int CmpMedianDesc(CallsiteGroup a, CallsiteGroup b) => b.MedianBytesPerFrame.CompareTo(a.MedianBytesPerFrame);
        static int CmpMeanAsc(CallsiteGroup a, CallsiteGroup b) => a.MeanBytesPerFrame.CompareTo(b.MeanBytesPerFrame);
        static int CmpMeanDesc(CallsiteGroup a, CallsiteGroup b) => b.MeanBytesPerFrame.CompareTo(a.MeanBytesPerFrame);
        static int CmpMinAsc(CallsiteGroup a, CallsiteGroup b) => a.MinBytesPerFrame.CompareTo(b.MinBytesPerFrame);
        static int CmpMinDesc(CallsiteGroup a, CallsiteGroup b) => b.MinBytesPerFrame.CompareTo(a.MinBytesPerFrame);
        static int CmpMaxAsc(CallsiteGroup a, CallsiteGroup b) => a.MaxBytesPerFrame.CompareTo(b.MaxBytesPerFrame);
        static int CmpMaxDesc(CallsiteGroup a, CallsiteGroup b) => b.MaxBytesPerFrame.CompareTo(a.MaxBytesPerFrame);
        static int CmpRangeAsc(CallsiteGroup a, CallsiteGroup b) => a.RangeBytesPerFrame.CompareTo(b.RangeBytesPerFrame);
        static int CmpRangeDesc(CallsiteGroup a, CallsiteGroup b) => b.RangeBytesPerFrame.CompareTo(a.RangeBytesPerFrame);
        static int CmpFirstAsc(CallsiteGroup a, CallsiteGroup b) => a.FirstFrame.CompareTo(b.FirstFrame);
        static int CmpFirstDesc(CallsiteGroup a, CallsiteGroup b) => b.FirstFrame.CompareTo(a.FirstFrame);
        static int CmpNameAsc(CallsiteGroup a, CallsiteGroup b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);
        static int CmpNameDesc(CallsiteGroup a, CallsiteGroup b) => string.Compare(b.DisplayName, a.DisplayName, StringComparison.Ordinal);

        void SortInPlace()
        {
            m_FilteredGroups.Sort(m_SortAsc ? m_SortCol switch
            {
                SortCol.Bytes  => CmpBytesAsc,  SortCol.Count  => CmpCountAsc,
                SortCol.Avg    => CmpAvgAsc,     SortCol.Pct    => CmpPctAsc,
                SortCol.Median => CmpMedianAsc,  SortCol.Mean   => CmpMeanAsc,
                SortCol.Min    => CmpMinAsc,      SortCol.Max    => CmpMaxAsc,
                SortCol.Range  => CmpRangeAsc,   SortCol.First  => CmpFirstAsc,
                SortCol.Name   => CmpNameAsc,    _              => CmpBytesAsc
            } : m_SortCol switch
            {
                SortCol.Bytes  => CmpBytesDesc,  SortCol.Count  => CmpCountDesc,
                SortCol.Avg    => CmpAvgDesc,     SortCol.Pct    => CmpPctDesc,
                SortCol.Median => CmpMedianDesc,  SortCol.Mean   => CmpMeanDesc,
                SortCol.Min    => CmpMinDesc,      SortCol.Max    => CmpMaxDesc,
                SortCol.Range  => CmpRangeDesc,   SortCol.First  => CmpFirstDesc,
                SortCol.Name   => CmpNameDesc,    _              => CmpBytesDesc
            });
        }

        void SortAndRefresh()
        {
            SortInPlace();
            RefreshMarkerListView();
        }

        void RefreshMarkerListView()
        {
            m_MarkerListView.itemsSource = m_FilteredGroups;
            m_MarkerListView.Rebuild();
        }

        void OnMarkerColumnSortingChanged()
        {
            using var e = m_MarkerListView.sortedColumns.GetEnumerator();
            if (!e.MoveNext()) return;
            var desc = e.Current;

            m_SortCol = desc.columnName switch
            {
                k_ColSite   => SortCol.Name,
                k_ColBytes  => SortCol.Bytes,
                k_ColCount  => SortCol.Count,
                k_ColAvg    => SortCol.Avg,
                k_ColPct    => SortCol.Pct,
                k_ColMedian => SortCol.Median,
                k_ColMean   => SortCol.Mean,
                k_ColMin    => SortCol.Min,
                k_ColMax    => SortCol.Max,
                k_ColRange  => SortCol.Range,
                k_ColFirst  => SortCol.First,
                _           => m_SortCol
            };
            m_SortAsc = desc.direction == SortDirection.Ascending;

            SortInPlace();
            RefreshMarkerListView();
        }

        void RestoreMarkerSortIndicator()
        {
            string colName = m_SortCol switch
            {
                SortCol.Name   => k_ColSite,
                SortCol.Bytes  => k_ColBytes,
                SortCol.Count  => k_ColCount,
                SortCol.Avg    => k_ColAvg,
                SortCol.Pct    => k_ColPct,
                SortCol.Median => k_ColMedian,
                SortCol.Mean   => k_ColMean,
                SortCol.Min    => k_ColMin,
                SortCol.Max    => k_ColMax,
                SortCol.Range  => k_ColRange,
                SortCol.First  => k_ColFirst,
                _              => k_ColBytes
            };

            var dir = m_SortAsc ? SortDirection.Ascending : SortDirection.Descending;
            m_MarkerListView.sortColumnDescriptions.Clear();
            m_MarkerListView.sortColumnDescriptions.Add(
                new SortColumnDescription { columnName = colName, direction = dir });
        }

        // ═══════════════════════════════════════════════════
        //  MARKER LIST — MULTI-COLUMN, no allocs in bind
        // ═══════════════════════════════════════════════════

        VisualElement MakeMarkerSiteCell()
        {
            var lbl = new Label { style = { fontSize = 11, flexGrow = 1, unityTextAlign = TextAnchor.MiddleLeft,
                overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } };
            lbl.AddManipulator(new ContextualMenuManipulator(OnMarkerRowContextMenu));
            return lbl;
        }

        VisualElement MakeMarkerCellWithMenu()
        {
            var lbl = new Label { style = { fontSize = 11, flexGrow = 1, unityTextAlign = TextAnchor.MiddleLeft } };
            lbl.AddManipulator(new ContextualMenuManipulator(OnMarkerRowContextMenu));
            return lbl;
        }

        void BindMarkerSite(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_FilteredGroups.Count) return;
            var g = m_FilteredGroups[index];
            var lbl = (Label)cell;
            lbl.text = g.DisplayName;
            lbl.tooltip = g.DisplayName;
            lbl.userData = g;
        }

        void BindMarkerBytes(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_FilteredGroups.Count) return;
            var g = m_FilteredGroups[index];
            var lbl = (Label)cell;
            lbl.text = g.FormattedBytes;
            lbl.style.color = GCAllocSettings.ColorForBytes(g.TotalBytes);
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.userData = g;
        }

        void BindMarkerCount(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_FilteredGroups.Count) return;
            var lbl = (Label)cell;
            lbl.text = m_FilteredGroups[index].FormattedCount;
            lbl.userData = m_FilteredGroups[index];
        }

        void BindMarkerAvg(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_FilteredGroups.Count) return;
            var lbl = (Label)cell;
            lbl.text = m_FilteredGroups[index].FormattedAvg;
            lbl.userData = m_FilteredGroups[index];
        }

        void BindMarkerPct(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_FilteredGroups.Count) return;
            var lbl = (Label)cell;
            lbl.text = m_FilteredGroups[index].FormattedPct;
            lbl.userData = m_FilteredGroups[index];
        }

        void BindMarkerMedian(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_FilteredGroups.Count) return;
            var lbl = (Label)cell;
            lbl.text = m_FilteredGroups[index].FormattedMedian;
            lbl.userData = m_FilteredGroups[index];
        }

        void BindMarkerMean(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_FilteredGroups.Count) return;
            var lbl = (Label)cell;
            lbl.text = m_FilteredGroups[index].FormattedMean;
            lbl.userData = m_FilteredGroups[index];
        }

        void BindMarkerMin(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_FilteredGroups.Count) return;
            var lbl = (Label)cell;
            lbl.text = m_FilteredGroups[index].FormattedMin;
            lbl.userData = m_FilteredGroups[index];
        }

        void BindMarkerMax(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_FilteredGroups.Count) return;
            var g = m_FilteredGroups[index];
            var lbl = (Label)cell;
            lbl.text = g.FormattedMax;
            lbl.style.color = GCAllocSettings.ColorForBytes(g.MaxBytesPerFrame);
            lbl.userData = g;
        }

        void BindMarkerRange(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_FilteredGroups.Count) return;
            var lbl = (Label)cell;
            lbl.text = m_FilteredGroups[index].FormattedRange;
            lbl.userData = m_FilteredGroups[index];
        }

        void BindMarkerFirst(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_FilteredGroups.Count) return;
            var lbl = (Label)cell;
            lbl.text = m_FilteredGroups[index].FormattedFirst;
            lbl.userData = m_FilteredGroups[index];
        }

        static void UnbindMarkerStyledCell(VisualElement cell, int index)
        {
            cell.style.color = StyleKeyword.Null;
            cell.style.unityFontStyleAndWeight = StyleKeyword.Null;
        }

        void OnMarkerRowContextMenu(ContextualMenuPopulateEvent evt)
        {
            var group = (evt.currentTarget as VisualElement)?.userData as CallsiteGroup;
            if (group == null) return;

            evt.menu.AppendAction("Copy Name", _ =>
                EditorGUIUtility.systemCopyBuffer = group.DisplayName);

            evt.menu.AppendAction("Add to Name Filter", _ =>
            {
                if (m_NameFilter != null) m_NameFilter.value = group.DisplayName;
            });

            evt.menu.AppendAction("Add to Exclude Filter", _ =>
            {
                if (m_ExcludeFilter != null) m_ExcludeFilter.value = group.DisplayName;
            });

            // Open Source File — disabled when no source info
            bool canOpen = group.ResolvedCallStack != null &&
                           group.ResolvedCallStack.Count > 0 &&
                           CanOpenScript(group.ResolvedCallStack[0]);
            evt.menu.AppendAction("Open Source File",
                _ => { if (group.ResolvedCallStack?.Count > 0) OpenScript(group.ResolvedCallStack[0]); },
                canOpen ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        void OnMarkerSelectionChanged(IEnumerable<object> selection)
        {
            CallsiteGroup group = null;
            foreach (object obj in selection) { group = obj as CallsiteGroup; break; }
            if (group == null) { ClearMarkerSummary(); ClearGraphOverlay(); return; }
            UpdateMarkerSummary(group);
            UpdateGraphOverlay(group);
            if (!m_IsLoadedSnapshot && group.Allocations.Count > 0)
                SelectInCpuModule(group.Allocations[0]);
        }

        // ═══════════════════════════════════════════════════
        //  RIGHT PANEL UPDATES
        // ═══════════════════════════════════════════════════

        void ShowNoDataState(bool show)
        {
            m_NoDataLabel.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            m_MarkerSummaryRoot.style.display = show ? DisplayStyle.None : DisplayStyle.Flex;
            m_TopOffendersFoldout.style.display = show ? DisplayStyle.None : DisplayStyle.Flex;
            // Graph visibility is managed by RebuildGraph(); only hide here
            if (show && m_GraphController != null) m_GraphController.Root.style.display = DisplayStyle.None;
        }

        void UpdateDataSummary()
        {
            int count = m_Snapshot.FrameEnd - m_Snapshot.FrameStart + 1;
            bool hasData = count > 0 && m_Snapshot.TotalCount > 0;

            m_FrameCountLabel.text = hasData ? string.Concat("Frame Count: ", count.ToString()) : "Frame Count: —";
            m_FrameRangeLabel.text = hasData
                ? string.Concat("Frame Range: ", GCAllocUtils.DisplayFrame(m_Snapshot.FrameStart).ToString(), "–", GCAllocUtils.DisplayFrame(m_Snapshot.FrameEnd).ToString())
                : "Frame Range: —";
            m_TotalGcLabel.text = hasData ? string.Concat("Total GC: ", GCAllocUtils.FormatBytes(m_Snapshot.TotalBytes)) : "Total GC: —";
            m_TotalAllocsLabel.text = hasData ? string.Concat("Total Allocs: ", m_Snapshot.TotalCount.ToString()) : "Total Allocs: —";
            m_UniqueSitesLabel.text = hasData ? string.Concat("Unique Sites: ", m_ActiveGroups.Count.ToString()) : "Unique Sites: —";
        }

        // ═══════════════════════════════════════════════════
        //  TOP OFFENDERS
        // ═══════════════════════════════════════════════════

        void BuildTopOffenders()
        {
            // Top 10 groups by total bytes — sort a copy of active groups
            m_TopByTotalBytes.Clear();
            for (int i = 0; i < m_ActiveGroups.Count; i++)
                m_TopByTotalBytes.Add(m_ActiveGroups[i]);
            m_TopByTotalBytes.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
            if (m_TopByTotalBytes.Count > 10)
                m_TopByTotalBytes.RemoveRange(10, m_TopByTotalBytes.Count - 10);

            // Top 10 single largest allocations
            m_TopSingleAllocs.Clear();
            for (int i = 0; i < m_Snapshot.RawAllocations.Count; i++)
            {
                var a = m_Snapshot.RawAllocations[i];
                if (m_TopSingleAllocs.Count < 10)
                {
                    InsertSorted(m_TopSingleAllocs, a);
                }
                else if (a.Bytes > m_TopSingleAllocs[m_TopSingleAllocs.Count - 1].Bytes)
                {
                    m_TopSingleAllocs.RemoveAt(m_TopSingleAllocs.Count - 1);
                    InsertSorted(m_TopSingleAllocs, a);
                }
            }

            PopulateTopOffendersUI();
        }

        static void InsertSorted(List<RawAllocation> list, RawAllocation item)
        {
            int pos = 0;
            for (; pos < list.Count; pos++)
                if (item.Bytes > list[pos].Bytes) break;
            list.Insert(pos, item);
        }

        void PopulateTopOffendersUI()
        {
            m_TopByTotalContainer.Clear();
            for (int i = 0; i < m_TopByTotalBytes.Count; i++)
            {
                var g = m_TopByTotalBytes[i];
                var row = MakeTopOffenderRow(
                    i + 1, g.FormattedBytes, g.FormattedPct, g.DisplayName, g.Key);
                m_TopByTotalContainer.Add(row);
            }

            m_TopSpikesContainer.Clear();
            for (int i = 0; i < m_TopSingleAllocs.Count; i++)
            {
                var a = m_TopSingleAllocs[i];
                m_SharedSB.Clear();
                m_SharedSB.Append(a.FormattedBytes);
                m_SharedSB.Append("  frame ");
                m_SharedSB.Append(a.FormattedFrame);
                string info = m_SharedSB.ToString();

                string spikeName = m_ShowAssembly ? a.DisplayNameWithAssembly : a.DisplayName;
                var row = MakeSpikeRow(i + 1, info, spikeName, a);
                m_TopSpikesContainer.Add(row);
            }
        }

        VisualElement MakeTopOffenderRow(int rank, string bytes, string pct, string name, string groupKey)
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    paddingTop = 2, paddingBottom = 2, paddingLeft = 4, paddingRight = 2 },
                userData = groupKey
            };
            AddRowHover(row);
            row.RegisterCallback<ClickEvent>(OnTopOffenderClicked);

            row.Add(new Label(string.Concat(rank.ToString(), "."))
            {
                style = { width = 20, fontSize = 10, color = k_SubtleText },
                pickingMode = PickingMode.Ignore
            });
            row.Add(new Label(bytes)
            {
                style = { width = 70, fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold,
                    color = GCAllocSettings.MediumColor },
                pickingMode = PickingMode.Ignore
            });
            row.Add(new Label(pct)
            {
                style = { width = 40, fontSize = 10, color = k_SubtleText },
                pickingMode = PickingMode.Ignore
            });

            row.Add(new Label(name)
            {
                style = { flexGrow = 1, fontSize = 10, overflow = Overflow.Hidden,
                    textOverflow = TextOverflow.Ellipsis },
                tooltip = name,
                pickingMode = PickingMode.Ignore
            });

            return row;
        }

        VisualElement MakeSpikeRow(int rank, string info, string name, RawAllocation alloc)
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    paddingTop = 2, paddingBottom = 2, paddingLeft = 4, paddingRight = 2 },
                userData = alloc
            };
            AddRowHover(row);
            row.RegisterCallback<ClickEvent>(OnSpikeClicked);

            row.Add(new Label(string.Concat(rank.ToString(), "."))
            {
                style = { width = 20, fontSize = 10, color = k_SubtleText },
                pickingMode = PickingMode.Ignore
            });
            row.Add(new Label(info)
            {
                style = { width = 110, fontSize = 10, unityFontStyleAndWeight = FontStyle.Bold,
                    color = GCAllocSettings.HighColor },
                pickingMode = PickingMode.Ignore
            });

            row.Add(new Label(name)
            {
                style = { flexGrow = 1, fontSize = 10, overflow = Overflow.Hidden,
                    textOverflow = TextOverflow.Ellipsis },
                tooltip = name,
                pickingMode = PickingMode.Ignore
            });

            return row;
        }

        void OnTopOffenderClicked(ClickEvent evt)
        {
            var key = ((VisualElement)evt.currentTarget).userData as string;
            if (key == null) return;

            // Find the group in the filtered list and select it
            for (int i = 0; i < m_FilteredGroups.Count; i++)
            {
                if (m_FilteredGroups[i].Key == key)
                {
                    m_MarkerListView.selectedIndex = i;
                    m_MarkerListView.ScrollToItem(i);
                    return;
                }
            }
        }

        void OnSpikeClicked(ClickEvent evt)
        {
            var alloc = ((VisualElement)evt.currentTarget).userData as RawAllocation;
            if (alloc == null) return;

            // Find the group this alloc belongs to and select it
            string key = m_GroupByCallsite.value ? alloc.FullCallstackKey : alloc.TopFrameKey;
            if (alloc.ResolvedCallStack == null || alloc.ResolvedCallStack.Count == 0)
                key = string.Concat("no_cs|", alloc.ParentMethod);

            for (int i = 0; i < m_FilteredGroups.Count; i++)
            {
                if (m_FilteredGroups[i].Key == key)
                {
                    m_MarkerListView.selectedIndex = i;
                    m_MarkerListView.ScrollToItem(i);
                    break;
                }
            }

            // Also jump to the specific frame in the Profiler
            SelectInCpuModule(alloc);
        }

        void UpdateMarkerSummary(CallsiteGroup group)
        {
            ShowNoDataState(false);

            // Name + source
            string topMethod = group.ResolvedCallStack != null && group.ResolvedCallStack.Count > 0
                ? group.ResolvedCallStack[0].RawMethodName : group.DisplayName;

            m_MarkerNameLabel.text = GCAllocUtils.StripAssembly(topMethod);

            if (group.ResolvedCallStack != null && group.ResolvedCallStack.Count > 0)
            {
                var top = group.ResolvedCallStack[0];
                if (!string.IsNullOrEmpty(top.SourceFile))
                {
                    string fn = Path.GetFileName(top.SourceFile);
                    m_MarkerSourceLabel.text = top.SourceLine > 0
                        ? string.Concat(fn, ":", top.SourceLine.ToString()) : fn;
                }
                else
                {
                    string asm = GCAllocUtils.ExtractAssembly(top.RawMethodName);
                    m_MarkerSourceLabel.text = asm.Length > 0 ? string.Concat("[", asm, "]") : "";
                }
            }
            else
                m_MarkerSourceLabel.text = "";

            // Stats
            m_SharedSB.Clear();
            m_SharedSB.Append("Total: "); m_SharedSB.Append(group.FormattedBytes);
            m_SharedSB.Append("    Count: "); m_SharedSB.Append(group.FormattedCount);
            m_SharedSB.Append("    Avg: "); m_SharedSB.Append(group.FormattedAvg);
            m_SharedSB.Append("    "); m_SharedSB.Append(group.FormattedPct); m_SharedSB.Append(" of total");
            m_MarkerStatsLabel.text = m_SharedSB.ToString();

            // Call stack
            BuildCallStackDisplay(group);

            // Individual allocations
            m_SelectedAllocations = group.Allocations;
            SortAllocsInPlace();
            m_AllocListView.itemsSource = m_SelectedAllocations;
            m_AllocListView.Rebuild();
            m_AllocListView.selectedIndex = m_SelectedAllocations.Count > 0 ? 0 : -1;
        }

        void BuildCallStackDisplay(CallsiteGroup group)
        {
            m_CallStackContainer.Clear();

            if (group.ResolvedCallStack == null || group.ResolvedCallStack.Count == 0)
            {
                // Show hierarchy path as a visual breadcrumb trail
                string hierarchy = group.Allocations.Count > 0
                    ? group.Allocations[0].HierarchyPath : null;

                if (!string.IsNullOrEmpty(hierarchy))
                {
                    m_CallStackContainer.Add(new Label("Profiler Hierarchy")
                    {
                        style = { fontSize = 10, color = k_SubtleText, marginBottom = 2,
                            unityFontStyleAndWeight = FontStyle.Bold }
                    });

                    string[] segments = hierarchy.Split(new[] { " > " }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < segments.Length; i++)
                    {
                        bool isLast = i == segments.Length - 1;
                        m_SharedSB.Clear();
                        m_SharedSB.Append(isLast ? "→ " : "  ");
                        m_SharedSB.Append(segments[i]);

                        m_CallStackContainer.Add(new Label(m_SharedSB.ToString())
                        {
                            style = { fontSize = 11, color = isLast ? k_TopFrame : k_CallerFrame,
                                paddingTop = 1, paddingBottom = 1 }
                        });
                    }
                }

                m_CallStackContainer.Add(new Label("Enable Call Stacks in Profiler toolbar for full detail.")
                {
                    style = { fontSize = 10, color = k_SubtleText, marginTop = 4,
                        whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Italic }
                });
                return;
            }

            int maxF = group.ResolvedCallStack.Count < MAX_CALLSTACK_FRAMES
                ? group.ResolvedCallStack.Count : MAX_CALLSTACK_FRAMES;

            for (int i = 0; i < maxF; i++)
            {
                var frame = group.ResolvedCallStack[i];
                var frameRow = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, alignItems = Align.Center,
                        paddingTop = 1, paddingBottom = 1 }
                };

                string display = FormatStackFrameDisplay(frame, i);
                var frameLbl = new Label(display)
                {
                    style = { fontSize = 11, color = i == 0 ? k_TopFrame : k_CallerFrame,
                        flexGrow = 1, flexShrink = 1, overflow = Overflow.Hidden,
                        textOverflow = TextOverflow.Ellipsis }
                };

                m_SharedSB.Clear();
                m_SharedSB.Append(frame.RawMethodName);
                if (!string.IsNullOrEmpty(frame.SourceFile))
                {
                    m_SharedSB.Append('\n');
                    m_SharedSB.Append(frame.SourceFile);
                    m_SharedSB.Append(':');
                    m_SharedSB.Append(frame.SourceLine);
                }
                frameLbl.tooltip = m_SharedSB.ToString();
                frameRow.Add(frameLbl);

                // Context menu — closures acceptable (not a hot path, rebuilt per selection)
                var capturedFrameForMenu = frame;
                frameRow.AddManipulator(new ContextualMenuManipulator(menuEvt =>
                {
                    menuEvt.menu.AppendAction("Copy Method Name", _ =>
                        EditorGUIUtility.systemCopyBuffer = capturedFrameForMenu.RawMethodName);

                    bool canOpenSource = CanOpenScript(capturedFrameForMenu);
                    menuEvt.menu.AppendAction("Open Source File",
                        _ => OpenScript(capturedFrameForMenu),
                        canOpenSource ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                }));

                // Open script button
                if (CanOpenScript(frame))
                {
                    // This button is built once per call stack display (not per frame/scroll),
                    // so the closure here is acceptable — it's not a hot path.
                    var capturedFrame = frame;
                    var openBtn = new Button(() => OpenScript(capturedFrame))
                    {
                        text = "📄",
                        tooltip = "Open in IDE",
                        style = { width = 24, height = 16, fontSize = 10,
                            paddingTop = 0, paddingBottom = 0,
                            paddingLeft = 2, paddingRight = 2, marginLeft = 4 }
                    };
                    frameRow.Add(openBtn);
                }

                m_CallStackContainer.Add(frameRow);
            }

            if (group.ResolvedCallStack.Count > MAX_CALLSTACK_FRAMES)
            {
                m_SharedSB.Clear();
                m_SharedSB.Append("  ... +");
                m_SharedSB.Append(group.ResolvedCallStack.Count - MAX_CALLSTACK_FRAMES);
                m_SharedSB.Append(" more frames");
                m_CallStackContainer.Add(new Label(m_SharedSB.ToString())
                {
                    style = { fontSize = 10, color = k_SubtleText }
                });
            }
        }

        void ClearMarkerSummary()
        {
            m_MarkerNameLabel.text = "—";
            m_MarkerSourceLabel.text = "";
            m_MarkerStatsLabel.text = "";
            m_CallStackContainer.Clear();
            m_SelectedAllocations = new List<RawAllocation>(0);
            m_AllocListView.itemsSource = m_SelectedAllocations;
            m_AllocListView.Rebuild();
        }

        // ═══════════════════════════════════════════════════
        //  ALLOC LIST — MULTI-COLUMN, no allocs in bind
        // ═══════════════════════════════════════════════════

        VisualElement MakeAllocCellWithMenu()
        {
            var lbl = new Label { style = { fontSize = 11, flexGrow = 1, unityTextAlign = TextAnchor.MiddleLeft } };
            lbl.AddManipulator(new ContextualMenuManipulator(OnAllocRowContextMenu));
            return lbl;
        }

        VisualElement MakeAllocThreadCell()
        {
            var lbl = new Label { style = { fontSize = 11, flexGrow = 1, unityTextAlign = TextAnchor.MiddleLeft,
                overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } };
            lbl.AddManipulator(new ContextualMenuManipulator(OnAllocRowContextMenu));
            return lbl;
        }

        void BindAllocNum(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_SelectedAllocations.Count) return;
            var lbl = (Label)cell;
            lbl.text = (index + 1).ToString();
            lbl.userData = m_SelectedAllocations[index];
        }

        void BindAllocSize(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_SelectedAllocations.Count) return;
            var a = m_SelectedAllocations[index];
            var lbl = (Label)cell;
            lbl.text = a.FormattedBytes;
            lbl.userData = a;
        }

        void BindAllocFrame(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_SelectedAllocations.Count) return;
            var lbl = (Label)cell;
            lbl.text = m_SelectedAllocations[index].FormattedFrame;
            lbl.userData = m_SelectedAllocations[index];
        }

        void BindAllocThread(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_SelectedAllocations.Count) return;
            var a = m_SelectedAllocations[index];
            var lbl = (Label)cell;
            lbl.text = a.ThreadDisplayName;
            lbl.tooltip = a.HierarchyPath;
            lbl.userData = a;
        }

        void OnAllocRowContextMenu(ContextualMenuPopulateEvent evt)
        {
            var alloc = (evt.currentTarget as VisualElement)?.userData as RawAllocation;
            if (alloc == null) return;

            evt.menu.AppendAction("Jump to Frame in Profiler", _ =>
            {
                if (!m_IsLoadedSnapshot) SelectInCpuModule(alloc);
            },
            m_IsLoadedSnapshot ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);

            var sb = new StringBuilder(128);
            sb.Append(alloc.FormattedBytes);
            sb.Append(" | Frame "); sb.Append(alloc.FormattedFrame);
            sb.Append(" | "); sb.Append(alloc.ThreadDisplayName);
            sb.Append(" | "); sb.Append(alloc.HierarchyPath);
            string details = sb.ToString();

            evt.menu.AppendAction("Copy Details", _ =>
                EditorGUIUtility.systemCopyBuffer = details);
        }

        void OnAllocSelectionChanged(IEnumerable<object> selection)
        {
            RawAllocation alloc = null;
            foreach (object obj in selection) { alloc = obj as RawAllocation; break; }
            if (alloc != null && !m_IsLoadedSnapshot) SelectInCpuModule(alloc);
        }

        // ═══════════════════════════════════════════════════
        //  CPU MODULE SELECTION — lazy ref
        // ═══════════════════════════════════════════════════

        void EnsureProfilerRef()
        {
            if (m_ProfilerWindow != null && m_CpuController != null) return;

            if (!EditorWindow.HasOpenInstances<ProfilerWindow>())
            {
                m_ProfilerWindow = null;
                m_CpuController = null;
                return;
            }

            if (m_ProfilerWindow == null)
                m_ProfilerWindow = EditorWindow.GetWindow<ProfilerWindow>(false, null, false);

            if (m_CpuController == null && m_ProfilerWindow != null)
            {
                try
                {
                    m_CpuController = m_ProfilerWindow.GetFrameTimeViewSampleSelectionController(
                        ProfilerWindow.cpuModuleIdentifier);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(string.Concat("[GC Alloc Analyzer] CPU controller: ", e.Message));
                }
            }
        }

        void SelectInCpuModule(RawAllocation alloc)
        {
            EnsureProfilerRef();
            if (m_ProfilerWindow == null || m_CpuController == null) return;

            try
            {
                m_ProfilerWindow.selectedFrameIndex = alloc.FrameIndex;
                var sel = new ProfilerTimeSampleSelection(
                    alloc.FrameIndex, alloc.ThreadGroupName, alloc.ThreadName,
                    alloc.ThreadId, alloc.RawSampleIndex);
                m_CpuController.SetSelection(sel);
            }
            catch (Exception e)
            {
                Debug.LogWarning(string.Concat(
                    "[GC Alloc Analyzer] Selection failed frame=", alloc.FrameIndex.ToString(),
                    " sample=", alloc.RawSampleIndex.ToString(), ": ", e.Message));
            }
        }

        // ═══════════════════════════════════════════════════
        //  SCRIPT OPENING — delegated to ScriptOpener
        // ═══════════════════════════════════════════════════

        bool CanOpenScript(ResolvedFrame frame) => m_ScriptOpener.CanOpen(frame);
        void OpenScript(ResolvedFrame frame) => m_ScriptOpener.Open(frame);

        // ═══════════════════════════════════════════════════
        //  FORMATTING — uses shared StringBuilder (instance)
        // ═══════════════════════════════════════════════════

        string FormatStackFrameDisplay(ResolvedFrame frame, int depth)
        {
            m_SharedSB.Clear();
            m_SharedSB.Append(depth == 0 ? "→ " : "  ");
            m_SharedSB.Append(GCAllocUtils.StripAssembly(frame.RawMethodName));

            if (!string.IsNullOrEmpty(frame.SourceFile))
            {
                m_SharedSB.Append("  (");
                m_SharedSB.Append(Path.GetFileName(frame.SourceFile));
                if (frame.SourceLine > 0)
                {
                    m_SharedSB.Append(':');
                    m_SharedSB.Append(frame.SourceLine);
                }
                m_SharedSB.Append(')');
            }

            return m_SharedSB.ToString();
        }

        // ═══════════════════════════════════════════════════
        //  CALLSTACK KEY — uses shared StringBuilder
        // ═══════════════════════════════════════════════════

        string BuildNormalizedCallStackKey(List<ResolvedFrame> frames)
        {
            m_SharedSB.Clear();
            for (int i = 0; i < frames.Count; i++)
            {
                GCAllocUtils.AppendNormalizedKeyPart(m_SharedSB, frames[i]);
                m_SharedSB.Append('|');
            }
            return m_SharedSB.ToString();
        }

        string BuildHierarchyPath(List<DepthEntry> stack)
        {
            if (stack.Count == 0) return "<root>";
            m_SharedSB.Clear();
            for (int i = 0; i < stack.Count; i++)
            {
                if (i > 0) m_SharedSB.Append(" > ");
                m_SharedSB.Append(stack[i].Name);
            }
            return m_SharedSB.ToString();
        }

        // ═══════════════════════════════════════════════════
        //  UI DESIGN HELPERS
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Creates a foldout styled to match Unity's Profile Analyzer:
        /// bold header text, subtle background on the header bar, separator line.
        /// </summary>
        static Foldout MakeSectionFoldout(string title, bool defaultOpen = true)
        {
            var foldout = new Foldout { text = title, value = defaultOpen };
            foldout.style.marginBottom = 2;
            foldout.style.marginTop = 2;

            // Style the toggle (header bar) — subtle background like Profile Analyzer
            var toggle = foldout.Q<Toggle>();
            if (toggle != null)
            {
                toggle.style.backgroundColor = new Color(0.25f, 0.25f, 0.25f);
                toggle.style.paddingTop = 3;
                toggle.style.paddingBottom = 3;
                toggle.style.paddingLeft = 2;
                toggle.style.marginBottom = 2;
                toggle.style.borderBottomWidth = 1;
                toggle.style.borderBottomColor = new Color(0.15f, 0.15f, 0.15f);

                var label = toggle.Q<Label>();
                if (label != null)
                {
                    label.style.fontSize = 12;
                    label.style.unityFontStyleAndWeight = FontStyle.Bold;
                }
            }

            return foldout;
        }

        /// <summary>
        /// Adds hover highlight to a row VisualElement.
        /// </summary>
        static void AddRowHover(VisualElement row)
        {
            row.RegisterCallback<MouseEnterEvent>(OnRowHoverEnter);
            row.RegisterCallback<MouseLeaveEvent>(OnRowHoverLeave);
        }

        static void OnRowHoverEnter(MouseEnterEvent evt)
        {
            var el = evt.currentTarget as VisualElement;
            if (el != null) el.style.backgroundColor = k_HoverBg;
        }

        static void OnRowHoverLeave(MouseLeaveEvent evt)
        {
            var el = evt.currentTarget as VisualElement;
            if (el != null) el.style.backgroundColor = StyleKeyword.Null;
        }

    }
}
