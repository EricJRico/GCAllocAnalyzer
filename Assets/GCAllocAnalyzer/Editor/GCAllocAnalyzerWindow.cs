using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

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
        const float ALLOC_COL_NUM = 30;
        const float ALLOC_COL_SIZE = 70;
        const float ALLOC_COL_FRAME = 60;
        const int MARKER_ROW_HEIGHT = 22;
        const int ALLOC_ROW_HEIGHT = 20;
        const int MAX_CALLSTACK_FRAMES = 20;
        const string k_MainThread = "Main Thread";
        const string k_RenderThread = "Render Thread";

        static readonly Color k_Red = new(1f, 0.3f, 0.3f);
        static readonly Color k_Yellow = new(1f, 0.85f, 0.2f);
        static readonly Color k_DimGray = new(0.7f, 0.7f, 0.7f);
        static readonly Color k_TopFrame = new(0.9f, 0.9f, 0.6f);
        static readonly Color k_CallerFrame = new(0.55f, 0.55f, 0.55f);
        static readonly Color k_LinkBlue = new(0.4f, 0.7f, 1f);
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
        Button m_ThreadFilterBtn;
        Toggle m_GroupByCallsite;
        ListView m_MarkerListView;
        VisualElement m_MarkerHeaderRow;
        Label m_StatusLabel;
        Button m_SaveBtn;
        Button m_LoadBtn;

        // Right panel
        Label m_FrameCountLabel, m_FrameRangeLabel, m_TotalGcLabel;
        Label m_TotalAllocsLabel, m_UniqueSitesLabel;
        Label m_MarkerNameLabel, m_MarkerSourceLabel, m_MarkerStatsLabel;
        VisualElement m_CallStackContainer;
        ScrollView m_CallStackScroll;
        ListView m_AllocListView;
        VisualElement m_AllocHeaderRow;
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

        // ═══════════════════════════════════════════════════
        //  SORT
        // ═══════════════════════════════════════════════════

        enum SortCol { Bytes, Count, Avg, Pct, Name }

        // ═══════════════════════════════════════════════════
        //  DATA FIELDS — pre-allocated, reused via .Clear()
        // ═══════════════════════════════════════════════════

        // Core analysis data — serialized to survive domain reload
        [SerializeField] AnalysisSnapshot m_Snapshot = new();
        [SerializeField] SortCol m_SortCol = SortCol.Bytes;
        [SerializeField] bool m_SortAsc;
        [SerializeField] bool m_ShowAssembly;

        // Points to m_Snapshot.GroupsByFullCallstack or GroupsByTopFrame
        List<CallsiteGroup> m_ActiveGroups;

        readonly List<CallsiteGroup> m_FilteredGroups = new(256);
        List<RawAllocation> m_SelectedAllocations = new List<RawAllocation>(512);

        // Thread state (rebuilt from m_Snapshot.SortedThreadNames after reload)
        readonly HashSet<string> m_AllThreadNames = new();
        readonly HashSet<string> m_SelectedThreads = new();

        // Thread index: group key → set of thread names (built once per grouping)
        readonly Dictionary<string, HashSet<string>> m_GroupThreadIndex = new(256);

        // Reusable buffers
        readonly StringBuilder m_SharedSB = new(1024);
        readonly List<ulong> m_AddrBuffer = new(64);
        readonly List<ResolvedFrame> m_FrameBuffer = new(64);
        readonly List<DepthEntry> m_DepthStack = new(32);

        // Grouping work buffers
        readonly Dictionary<string, CallsiteGroup> m_GroupingDict = new(256);

        // Filter state cache (to detect actual changes)
        string m_LastNameFilter = "";
        int m_LastSelectedThreadCount = -1;

        // ═══════════════════════════════════════════════════
        //  PROFILER — lazy, only resolved on selection
        // ═══════════════════════════════════════════════════

        ProfilerWindow m_ProfilerWindow;
        IProfilerFrameTimeViewSampleSelectionController m_CpuController;
        readonly Dictionary<string, (MonoScript script, bool found)> m_ScriptCache = new();

        // ═══════════════════════════════════════════════════
        //  WINDOW LIFECYCLE
        // ═══════════════════════════════════════════════════

        [MenuItem("Window/Analysis/GC Alloc Analyzer")]
        public static GCAllocAnalyzerWindow ShowWindow()
        {
            var w = GetWindow<GCAllocAnalyzerWindow>("GC Alloc Analyzer");
            w.minSize = new Vector2(750, 420);
            w.Show();
            return w;
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexGrow = 1;
            root.style.minWidth = 700;

            root.Add(BuildToolbar());

            var splitView = new TwoPaneSplitView(0, 400, TwoPaneSplitViewOrientation.Horizontal);
            splitView.style.flexGrow = 1;
            splitView.Add(BuildLeftPanel());
            splitView.Add(BuildRightPanel());
            root.Add(splitView);

            root.Add(BuildStatusBar());
            ShowNoDataState(true);

            // Restore state after domain reload (e.g. script save)
            TryRestoreAfterReload();
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
            for (int i = 0; i < m_Snapshot.SortedThreadNames.Count; i++)
                m_AllThreadNames.Add(m_Snapshot.SortedThreadNames[i]);
            UpdateThreadButtonLabel();

            // Rebuild both groupings from raw data
            BuildGrouping(true, m_Snapshot.GroupsByFullCallstack);
            BuildGrouping(false, m_Snapshot.GroupsByTopFrame);

            m_ActiveGroups = m_GroupByCallsite.value
                ? m_Snapshot.GroupsByFullCallstack : m_Snapshot.GroupsByTopFrame;
            BuildThreadIndex(m_ActiveGroups);
            BuildTopOffenders();
            ApplyFilters();
            UpdateDataSummary();
            ShowNoDataState(m_ActiveGroups.Count == 0);

            // Restore frame range in UI
            m_StartFrameField.value = m_Snapshot.FrameStart;
            m_EndFrameField.value = m_Snapshot.FrameEnd;
            UpdateFrameRangeInfo();

            m_SharedSB.Clear();
            m_SharedSB.Append("Restored: ");
            m_SharedSB.Append(m_Snapshot.TotalCount);
            m_SharedSB.Append(" allocs across frames ");
            m_SharedSB.Append(m_Snapshot.FrameStart);
            m_SharedSB.Append('–');
            m_SharedSB.Append(m_Snapshot.FrameEnd);
            m_StatusLabel.text = m_SharedSB.ToString();
            m_SaveBtn?.SetEnabled(true);
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

            m_LoadBtn = new Button(OnLoadSnapshot) { text = "Load", style = { marginRight = 12 } };
            bar.Add(m_LoadBtn);

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
            for (int i = 0; i < m_Snapshot.SortedThreadNames.Count; i++)
                m_AllThreadNames.Add(m_Snapshot.SortedThreadNames[i]);
            UpdateThreadButtonLabel();

            BuildGrouping(true, m_Snapshot.GroupsByFullCallstack);
            BuildGrouping(false, m_Snapshot.GroupsByTopFrame);

            m_ActiveGroups = m_GroupByCallsite.value
                ? m_Snapshot.GroupsByFullCallstack : m_Snapshot.GroupsByTopFrame;
            BuildThreadIndex(m_ActiveGroups);
            BuildTopOffenders();
            ApplyFilters();
            UpdateDataSummary();
            ShowNoDataState(m_ActiveGroups.Count == 0);

            m_StartFrameField.value = m_Snapshot.FrameStart;
            m_EndFrameField.value = m_Snapshot.FrameEnd;
            UpdateFrameRangeInfo();

            m_SaveBtn.SetEnabled(true);

            if (m_FilteredGroups.Count > 0)
                m_MarkerListView.selectedIndex = 0;

            m_SharedSB.Clear();
            m_SharedSB.Append("Loaded: ");
            m_SharedSB.Append(m_Snapshot.TotalCount);
            m_SharedSB.Append(" allocs across frames ");
            m_SharedSB.Append(m_Snapshot.FrameStart);
            m_SharedSB.Append('\u2013');
            m_SharedSB.Append(m_Snapshot.FrameEnd);
            m_SharedSB.Append(" from ");
            m_SharedSB.Append(Path.GetFileName(path));
            m_StatusLabel.text = m_SharedSB.ToString();
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
            m_NameFilter.RegisterValueChangedCallback(OnNameFilterChanged);
            filterRow1.Add(m_NameFilter);

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
                value = true,
                tooltip = "ON: group by full call stack.\nOFF: group by top frame only."
            };
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

            // Column headers
            m_MarkerHeaderRow = BuildMarkerHeaders();
            left.Add(m_MarkerHeaderRow);

            // Marker list (virtualized)
            m_MarkerListView = new ListView
            {
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                fixedItemHeight = MARKER_ROW_HEIGHT,
                selectionType = SelectionType.Single,
                showBorder = true,
                style = { flexGrow = 1 }
            };
            m_MarkerListView.makeItem = MakeMarkerRow;
            m_MarkerListView.bindItem = BindMarkerRow;
            m_MarkerListView.itemsSource = m_FilteredGroups;
            m_MarkerListView.selectionChanged += OnMarkerSelectionChanged;
            left.Add(m_MarkerListView);

            return left;
        }

        // Non-capturing filter callbacks
        void OnNameFilterChanged(ChangeEvent<string> evt) => ApplyFilters();
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
                menu.AddItem(new GUIContent(thread), on, OnThreadMenuToggle, thread);
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

        // ═══════════════════════════════════════════════════
        //  SORTABLE COLUMN HEADERS — userData, no closures
        // ═══════════════════════════════════════════════════

        VisualElement BuildMarkerHeaders()
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    borderBottomWidth = 2,
                    borderBottomColor = new Color(0.4f, 0.4f, 0.4f),
                    paddingBottom = 3, paddingTop = 2,
                    paddingLeft = 4, paddingRight = 4,
                    flexShrink = 0
                }
            };

            row.Add(MakeSortHeader("Bytes", COL_BYTES, SortCol.Bytes));
            row.Add(MakeSortHeader("Count", COL_COUNT, SortCol.Count));
            row.Add(MakeSortHeader("Avg", COL_AVG, SortCol.Avg));
            row.Add(MakeSortHeader("%", COL_PCT, SortCol.Pct));
            row.Add(MakeSortHeader("Allocation Site", 0, SortCol.Name, 1));

            return row;
        }

        Label MakeSortHeader(string text, float width, SortCol col, float grow = 0)
        {
            string arrow = m_SortCol == col ? (m_SortAsc ? " ▲" : " ▼") : "";
            var lbl = new Label(string.Concat(text, arrow))
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11 },
                userData = col
            };
            if (width > 0) lbl.style.width = width;
            if (grow > 0) lbl.style.flexGrow = grow;

            lbl.RegisterCallback<MouseEnterEvent>(OnSortHeaderEnter);
            lbl.RegisterCallback<MouseLeaveEvent>(OnSortHeaderLeave);
            lbl.RegisterCallback<ClickEvent>(OnSortHeaderClicked);
            return lbl;
        }

        static void OnSortHeaderEnter(MouseEnterEvent evt)
        {
            ((VisualElement)evt.target).style.color = k_LinkBlue;
        }

        static void OnSortHeaderLeave(MouseLeaveEvent evt)
        {
            ((VisualElement)evt.target).style.color = StyleKeyword.Null;
        }

        void OnSortHeaderClicked(ClickEvent evt)
        {
            var col = (SortCol)((VisualElement)evt.target).userData;
            if (m_SortCol == col) m_SortAsc = !m_SortAsc;
            else { m_SortCol = col; m_SortAsc = false; }
            SortAndRefresh();
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

            m_AllocHeaderRow = BuildAllocHeaders();
            siteFoldout.Add(m_AllocHeaderRow);

            m_AllocListView = new ListView
            {
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                fixedItemHeight = ALLOC_ROW_HEIGHT,
                selectionType = SelectionType.Single,
                showBorder = true,
                style = { flexGrow = 1, minHeight = 80 }
            };
            m_AllocListView.makeItem = MakeAllocRow;
            m_AllocListView.bindItem = BindAllocRow;
            m_AllocListView.itemsSource = m_SelectedAllocations;
            m_AllocListView.selectionChanged += OnAllocSelectionChanged;
            siteFoldout.Add(m_AllocListView);

            return right;
        }

        VisualElement BuildAllocHeaders()
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, paddingBottom = 2,
                    borderBottomWidth = 1, borderBottomColor = new Color(0.3f, 0.3f, 0.3f),
                    flexShrink = 0 }
            };
            row.Add(Lbl("#", ALLOC_COL_NUM, FontStyle.Bold));
            row.Add(MakeAllocSortHeader("Size", ALLOC_COL_SIZE, AllocSortCol.Size));
            row.Add(MakeAllocSortHeader("Frame", ALLOC_COL_FRAME, AllocSortCol.Frame));
            row.Add(Lbl("Thread", 0, FontStyle.Bold, 1));
            return row;
        }

        Label MakeAllocSortHeader(string text, float width, AllocSortCol col)
        {
            string arrow = m_AllocSortCol == col ? (m_AllocSortAsc ? " ▲" : " ▼") : "";
            var lbl = new Label(string.Concat(text, arrow))
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11, width = width },
                userData = col
            };
            lbl.RegisterCallback<MouseEnterEvent>(OnSortHeaderEnter);
            lbl.RegisterCallback<MouseLeaveEvent>(OnSortHeaderLeave);
            lbl.RegisterCallback<ClickEvent>(OnAllocSortHeaderClicked);
            return lbl;
        }

        void OnAllocSortHeaderClicked(ClickEvent evt)
        {
            var col = (AllocSortCol)((VisualElement)evt.target).userData;
            if (m_AllocSortCol == col) m_AllocSortAsc = !m_AllocSortAsc;
            else { m_AllocSortCol = col; m_AllocSortAsc = false; }
            SortAllocsAndRefresh();
        }

        void SortAllocsAndRefresh()
        {
            SortAllocsInPlace();
            ReplaceAllocHeaders();
            m_AllocListView.RefreshItems();
        }

        void SortAllocsInPlace()
        {
            if (m_SelectedAllocations == null || m_SelectedAllocations.Count == 0) return;
            int dir = m_AllocSortAsc ? 1 : -1;
            switch (m_AllocSortCol)
            {
                case AllocSortCol.Size:
                    m_SelectedAllocations.Sort((a, b) => dir * a.Bytes.CompareTo(b.Bytes));
                    break;
                case AllocSortCol.Frame:
                    m_SelectedAllocations.Sort((a, b) => dir * a.FrameIndex.CompareTo(b.FrameIndex));
                    break;
            }
        }

        void ReplaceAllocHeaders()
        {
            if (m_AllocHeaderRow == null) return;
            var parent = m_AllocHeaderRow.parent;
            int idx = parent.IndexOf(m_AllocHeaderRow);
            parent.Remove(m_AllocHeaderRow);
            m_AllocHeaderRow = BuildAllocHeaders();
            parent.Insert(idx, m_AllocHeaderRow);
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
            m_StartFrameField.value = first;
            m_EndFrameField.value = last;
            UpdateFrameRangeInfo();

            m_SharedSB.Clear();
            m_SharedSB.Append("Pulled: frames ");
            m_SharedSB.Append(first);
            m_SharedSB.Append('–');
            m_SharedSB.Append(last);
            m_SharedSB.Append(" (");
            m_SharedSB.Append(last - first + 1);
            m_SharedSB.Append(" frames). Adjust range if needed, then click Analyze.");
            m_StatusLabel.text = m_SharedSB.ToString();
        }

        void OnAnalyze()
        {
            int start = m_StartFrameField.value;
            int end = m_EndFrameField.value;
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
            m_ScriptCache.Clear();
            m_Snapshot.RawAllocations.Clear();
            m_AllThreadNames.Clear();
            m_SelectedThreads.Clear();
            bool anyCallStacks = false;
            long totalBytes = 0;

            int totalFrames = endFrame - startFrame + 1;

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

                    for (int threadIdx = 0; threadIdx < 64; threadIdx++)
                    {
                        using var raw = ProfilerDriver.GetRawFrameDataView(f, threadIdx);
                        if (!raw.valid) break;

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

                        m_AllThreadNames.Add(threadDisplay);
                        m_DepthStack.Clear();

                        for (int i = 0; i < raw.sampleCount; i++)
                        {
                            int markerId = raw.GetSampleMarkerId(i);
                            int childCount = raw.GetSampleChildrenCount(i);
                            string sampleName = raw.GetSampleName(i);

                            while (m_DepthStack.Count > 0 &&
                                   m_DepthStack[m_DepthStack.Count - 1].Remaining <= 0)
                                m_DepthStack.RemoveAt(m_DepthStack.Count - 1);

                            if (m_DepthStack.Count > 0)
                                m_DepthStack[m_DepthStack.Count - 1].Remaining--;

                            if (markerId == gcAllocId)
                            {
                                long bytes = raw.GetSampleMetadataAsLong(i, 0);
                                if (bytes <= 0) continue;
                                totalBytes += bytes;

                                // Resolve call stack
                                m_FrameBuffer.Clear();
                                m_AddrBuffer.Clear();
                                raw.GetSampleCallstack(i, m_AddrBuffer);

                                if (m_AddrBuffer.Count > 0)
                                {
                                    anyCallStacks = true;
                                    for (int a = 0; a < m_AddrBuffer.Count; a++)
                                    {
                                        var info = raw.ResolveMethodInfo(m_AddrBuffer[a]);
                                        if (string.IsNullOrEmpty(info.methodName)) continue;
                                        m_FrameBuffer.Add(new ResolvedFrame
                                        {
                                            RawMethodName = info.methodName.Trim(),
                                            SourceFile = (info.sourceFileName ?? "").Trim(),
                                            SourceLine = (int)info.sourceFileLine
                                        });
                                    }
                                }

                                string hierarchyPath = BuildHierarchyPath(m_DepthStack);
                                string parentMethod = m_DepthStack.Count > 0
                                    ? m_DepthStack[m_DepthStack.Count - 1].Name : "<root>";

                                // Copy frame buffer into a new list for this alloc
                                var resolvedCopy = new List<ResolvedFrame>(m_FrameBuffer.Count);
                                for (int fc = 0; fc < m_FrameBuffer.Count; fc++)
                                    resolvedCopy.Add(m_FrameBuffer[fc]);

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
                                    ResolvedCallStack = resolvedCopy,
                                    FormattedBytes = FormatBytes(bytes),
                                    FormattedFrame = f.ToString()
                                };

                                // Pre-compute keys
                                alloc.FullCallstackKey = resolvedCopy.Count > 0
                                    ? BuildNormalizedCallStackKey(resolvedCopy) : "";
                                alloc.TopFrameKey = resolvedCopy.Count > 0
                                    ? NormalizeKeyPart(resolvedCopy[0]) : "";
                                alloc.DisplayName = resolvedCopy.Count > 0
                                    ? FormatTopFrame(resolvedCopy[0])
                                    : StripAssembly(parentMethod);
                                alloc.DisplayNameWithAssembly = resolvedCopy.Count > 0
                                    ? FormatTopFrameWithAssembly(resolvedCopy[0])
                                    : StripLeadingColons(parentMethod);

                                m_Snapshot.RawAllocations.Add(alloc);
                            }

                            if (childCount > 0)
                                m_DepthStack.Add(new DepthEntry { Name = sampleName, Remaining = childCount });
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            m_Snapshot.TotalBytes = totalBytes;
            m_Snapshot.TotalCount = m_Snapshot.RawAllocations.Count;
            m_Snapshot.FrameStart = startFrame;
            m_Snapshot.FrameEnd = endFrame;
            m_Snapshot.HadCallStacks = anyCallStacks;

            // Build sorted thread names
            m_Snapshot.SortedThreadNames.Clear();
            foreach (string t in m_AllThreadNames)
                m_Snapshot.SortedThreadNames.Add(t);
            m_Snapshot.SortedThreadNames.Sort(StringComparer.Ordinal);

            UpdateThreadButtonLabel();

            // Build BOTH groupings once
            BuildGrouping(true, m_Snapshot.GroupsByFullCallstack);
            BuildGrouping(false, m_Snapshot.GroupsByTopFrame);

            // Set active based on current toggle
            m_ActiveGroups = m_GroupByCallsite.value ? m_Snapshot.GroupsByFullCallstack : m_Snapshot.GroupsByTopFrame;

            // Build thread index for active grouping
            BuildThreadIndex(m_ActiveGroups);

            // Build top offenders
            BuildTopOffenders();

            ApplyFilters();
            UpdateDataSummary();
            ShowNoDataState(m_ActiveGroups.Count == 0);
            m_SaveBtn?.SetEnabled(m_Snapshot.HasData);

            // Status
            m_SharedSB.Clear();
            m_SharedSB.Append("Analyzed ");
            m_SharedSB.Append(totalFrames);
            m_SharedSB.Append(" frames (");
            m_SharedSB.Append(startFrame);
            m_SharedSB.Append('–');
            m_SharedSB.Append(endFrame);
            m_SharedSB.Append(") | ");
            m_SharedSB.Append(FormatBytes(totalBytes));
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

            // Pre-compute display strings for each group
            for (int i = 0; i < target.Count; i++)
            {
                var g = target[i];
                g.Percentage = (float)g.TotalBytes / total * 100f;
                g.FormattedBytes = FormatBytes(g.TotalBytes);
                g.FormattedCount = string.Concat(g.Count.ToString(), "x");
                g.FormattedAvg = FormatBytes(g.TotalBytes / Math.Max(1, g.Count));

                m_SharedSB.Clear();
                m_SharedSB.Append(g.Percentage.ToString("F1"));
                m_SharedSB.Append('%');
                g.FormattedPct = m_SharedSB.ToString();
            }
        }

        void BuildThreadIndex(List<CallsiteGroup> groups)
        {
            m_GroupThreadIndex.Clear();
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var threads = new HashSet<string>();
                for (int j = 0; j < g.Allocations.Count; j++)
                    threads.Add(g.Allocations[j].ThreadDisplayName);
                m_GroupThreadIndex[g.Key] = threads;
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
        //  FILTERING & SORTING — no LINQ, reuse lists
        // ═══════════════════════════════════════════════════

        void ApplyFilters()
        {
            if (m_ActiveGroups == null) return;

            string nameFilter = m_NameFilter != null ? m_NameFilter.value : "";
            bool allThreads = m_SelectedThreads.Count == 0;

            m_FilteredGroups.Clear();
            for (int i = 0; i < m_ActiveGroups.Count; i++)
            {
                var g = m_ActiveGroups[i];

                // Name filter
                if (nameFilter.Length > 0 &&
                    g.DisplayName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
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

        void SortInPlace()
        {
            int dir = m_SortAsc ? 1 : -1;
            switch (m_SortCol)
            {
                case SortCol.Bytes:
                    m_FilteredGroups.Sort((a, b) => dir * a.TotalBytes.CompareTo(b.TotalBytes));
                    break;
                case SortCol.Count:
                    m_FilteredGroups.Sort((a, b) => dir * a.Count.CompareTo(b.Count));
                    break;
                case SortCol.Avg:
                    m_FilteredGroups.Sort((a, b) =>
                    {
                        long aa = a.TotalBytes / Math.Max(1, a.Count);
                        long bb = b.TotalBytes / Math.Max(1, b.Count);
                        return dir * aa.CompareTo(bb);
                    });
                    break;
                case SortCol.Pct:
                    m_FilteredGroups.Sort((a, b) => dir * a.Percentage.CompareTo(b.Percentage));
                    break;
                case SortCol.Name:
                    m_FilteredGroups.Sort((a, b) =>
                        dir * string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal));
                    break;
            }
        }

        void SortAndRefresh()
        {
            SortInPlace();
            ReplaceMarkerHeaders();
            RefreshMarkerListView();
        }

        void ReplaceMarkerHeaders()
        {
            var parent = m_MarkerHeaderRow.parent;
            int idx = parent.IndexOf(m_MarkerHeaderRow);
            parent.Remove(m_MarkerHeaderRow);
            m_MarkerHeaderRow = BuildMarkerHeaders();
            parent.Insert(idx, m_MarkerHeaderRow);
        }

        void RefreshMarkerListView()
        {
            m_MarkerListView.itemsSource = m_FilteredGroups;
            m_MarkerListView.Rebuild();
        }

        // ═══════════════════════════════════════════════════
        //  MARKER LIST — VIRTUALIZED, no allocs in bind
        // ═══════════════════════════════════════════════════

        static VisualElement MakeMarkerRow()
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    paddingLeft = 4, paddingRight = 4 }
            };

            row.Add(new Label { name = "bytes", style = { width = COL_BYTES, fontSize = 11 } });
            row.Add(new Label { name = "count", style = { width = COL_COUNT, fontSize = 11 } });
            row.Add(new Label { name = "avg", style = { width = COL_AVG, fontSize = 11 } });
            row.Add(new Label { name = "pct", style = { width = COL_PCT, fontSize = 11 } });
            row.Add(new Label { name = "site", style = { flexGrow = 1, fontSize = 11,
                overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } });

            return row;
        }

        void BindMarkerRow(VisualElement el, int index)
        {
            if (index < 0 || index >= m_FilteredGroups.Count) return;
            var g = m_FilteredGroups[index];

            var bytesLbl = el.Q<Label>("bytes");
            bytesLbl.text = g.FormattedBytes;
            bytesLbl.style.color = g.TotalBytes >= 10240 ? k_Red
                : g.TotalBytes >= 1024 ? k_Yellow : k_DimGray;
            bytesLbl.style.unityFontStyleAndWeight = FontStyle.Bold;

            el.Q<Label>("count").text = g.FormattedCount;
            el.Q<Label>("avg").text = g.FormattedAvg;
            el.Q<Label>("pct").text = g.FormattedPct;

            var siteLbl = el.Q<Label>("site");
            siteLbl.text = g.DisplayName;
            siteLbl.tooltip = g.DisplayName;
        }

        void OnMarkerSelectionChanged(IEnumerable<object> selection)
        {
            CallsiteGroup group = null;
            foreach (object obj in selection) { group = obj as CallsiteGroup; break; }
            if (group == null) { ClearMarkerSummary(); return; }
            UpdateMarkerSummary(group);
            if (group.Allocations.Count > 0)
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
        }

        void UpdateDataSummary()
        {
            int count = m_Snapshot.FrameEnd - m_Snapshot.FrameStart + 1;
            bool hasData = count > 0 && m_Snapshot.TotalCount > 0;

            m_FrameCountLabel.text = hasData ? string.Concat("Frame Count: ", count.ToString()) : "Frame Count: —";
            m_FrameRangeLabel.text = hasData
                ? string.Concat("Frame Range: ", m_Snapshot.FrameStart.ToString(), "–", m_Snapshot.FrameEnd.ToString())
                : "Frame Range: —";
            m_TotalGcLabel.text = hasData ? string.Concat("Total GC: ", FormatBytes(m_Snapshot.TotalBytes)) : "Total GC: —";
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
                    color = k_Yellow },
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
                    color = k_Red },
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

            m_MarkerNameLabel.text = StripAssembly(topMethod);

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
                    string asm = ExtractAssembly(top.RawMethodName);
                    m_MarkerSourceLabel.text = asm.Length > 0 ? string.Concat("[", asm, "]") : "";
                }
            }
            else
                m_MarkerSourceLabel.text = "";

            // Stats
            long avg = group.TotalBytes / Math.Max(1, group.Count);
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
        //  ALLOC LIST — VIRTUALIZED, pre-computed strings
        // ═══════════════════════════════════════════════════

        static VisualElement MakeAllocRow()
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    paddingLeft = 2, paddingRight = 2 }
            };
            row.Add(new Label { name = "num", style = { width = ALLOC_COL_NUM, fontSize = 11 } });
            row.Add(new Label { name = "size", style = { width = ALLOC_COL_SIZE, fontSize = 11 } });
            row.Add(new Label { name = "frame", style = { width = ALLOC_COL_FRAME, fontSize = 11 } });
            row.Add(new Label { name = "thread", style = { flexGrow = 1, fontSize = 11,
                overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } });
            return row;
        }

        void BindAllocRow(VisualElement el, int index)
        {
            if (index < 0 || index >= m_SelectedAllocations.Count) return;
            var a = m_SelectedAllocations[index];

            // index+1 display — avoid ToString in hot path by pre-checking
            // (ListView only binds visible rows so this is acceptable)
            el.Q<Label>("num").text = (index + 1).ToString();
            el.Q<Label>("size").text = a.FormattedBytes;
            el.Q<Label>("frame").text = a.FormattedFrame;
            el.Q<Label>("thread").text = a.ThreadDisplayName;
            el.tooltip = a.HierarchyPath;
        }

        void OnAllocSelectionChanged(IEnumerable<object> selection)
        {
            RawAllocation alloc = null;
            foreach (object obj in selection) { alloc = obj as RawAllocation; break; }
            if (alloc != null) SelectInCpuModule(alloc);
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
        //  SCRIPT OPENING — no path filtering
        // ═══════════════════════════════════════════════════

        bool CanOpenScript(ResolvedFrame frame)
        {
            if (!string.IsNullOrEmpty(frame.SourceFile))
                return FindScript(Path.GetFileNameWithoutExtension(frame.SourceFile)) != null;

            return FindScriptFromMethodName(frame.RawMethodName) != null;
        }

        void OpenScript(ResolvedFrame frame)
        {
            MonoScript script = null;
            int line = 1;

            if (!string.IsNullOrEmpty(frame.SourceFile))
            {
                script = FindScript(Path.GetFileNameWithoutExtension(frame.SourceFile));
                if (frame.SourceLine > 0) line = frame.SourceLine;
            }

            if (script == null)
            {
                script = FindScriptFromMethodName(frame.RawMethodName);
                if (script == null) return;

                // Try to find the method in the source text to jump to the right line
                if (frame.SourceLine <= 0)
                {
                    string methodName = ExtractMethodNameFromEnd(frame.RawMethodName);
                    if (methodName.Length > 0)
                    {
                        // Strip generic marker
                        int bt = methodName.IndexOf('`');
                        if (bt >= 0) methodName = methodName.Substring(0, bt);

                        string text = script.text;
                        int idx = text.IndexOf(methodName, StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            int lineCount = 1;
                            for (int c = 0; c < idx; c++)
                                if (text[c] == '\n') lineCount++;
                            line = lineCount;
                        }
                    }
                }
                else
                    line = frame.SourceLine;
            }

            if (script != null)
                AssetDatabase.OpenAsset(script, line);
        }

        /// <summary>
        /// Walk backwards through the segments of a method name to find a matching script.
        /// For IL2CPP (::), the class name is always the segment immediately before "::".
        /// For Mono (.), splits by '.' and walks backwards from second-to-last.
        /// Also handles nested classes "+" and generics "`".
        /// </summary>
        MonoScript FindScriptFromMethodName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            // Check cache first with full raw name as key
            if (m_ScriptCache.TryGetValue(raw, out var cached)) return cached.script;

            // Strip assembly prefix
            string clean = StripAssembly(raw);

            // Strip arguments
            int paren = clean.IndexOf('(');
            if (paren >= 0) clean = clean.Substring(0, paren).TrimEnd();

            // Handle IL2CPP "::" — format is Namespace::Class.Method
            // Class name is the first segment AFTER ::
            int dcolon = clean.IndexOf("::", StringComparison.Ordinal);
            if (dcolon >= 0)
            {
                string afterDcolon = clean.Substring(dcolon + 2);

                // afterDcolon is "Class.Method" or just "Method"
                // Walk backwards: last segment is method, everything before is class candidates
                string[] parts = afterDcolon.Split('.');

                // Walk backwards from second-to-last (skip method at end)
                for (int i = parts.Length - 2; i >= 0; i--)
                {
                    string candidate = parts[i];

                    int plus = candidate.IndexOf('+');
                    if (plus >= 0) candidate = candidate.Substring(0, plus);
                    int backtick = candidate.IndexOf('`');
                    if (backtick >= 0) candidate = candidate.Substring(0, backtick);

                    if (candidate.Length == 0) continue;

                    var script = FindScript(candidate);
                    if (script != null)
                    {
                        m_ScriptCache[raw] = (script, true);
                        return script;
                    }
                }

                m_ScriptCache[raw] = (null, false);
                return null;
            }

            // Mono style: "Namespace.ClassName.MethodName" — last is method, walk backwards for class
            int lastDotMono = clean.LastIndexOf('.');
            if (lastDotMono <= 0)
            {
                m_ScriptCache[raw] = (null, false);
                return null;
            }

            string typeSide = clean.Substring(0, lastDotMono);
            string[] segments = typeSide.Split('.');

            // Walk backwards — last segment is most likely the class
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                string candidate = segments[i];

                // Handle nested class: take part before '+'
                int p = candidate.IndexOf('+');
                if (p >= 0) candidate = candidate.Substring(0, p);

                // Handle generics: strip '`1'
                int bt = candidate.IndexOf('`');
                if (bt >= 0) candidate = candidate.Substring(0, bt);

                if (candidate.Length == 0) continue;

                var script = FindScript(candidate);
                if (script != null)
                {
                    m_ScriptCache[raw] = (script, true);
                    return script;
                }
            }

            // Also try nested class parts after '+'
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                int p = segments[i].IndexOf('+');
                if (p < 0) continue;

                string nested = segments[i].Substring(p + 1);
                int bt = nested.IndexOf('`');
                if (bt >= 0) nested = nested.Substring(0, bt);

                if (nested.Length == 0) continue;

                var script = FindScript(nested);
                if (script != null)
                {
                    m_ScriptCache[raw] = (script, true);
                    return script;
                }
            }

            m_ScriptCache[raw] = (null, false);
            return null;
        }

        /// <summary>
        /// Extracts just the method name (last segment) for line searching.
        /// </summary>
        static string ExtractMethodNameFromEnd(string raw)
        {
            string clean = StripAssembly(raw);
            int paren = clean.IndexOf('(');
            if (paren >= 0) clean = clean.Substring(0, paren).TrimEnd();

            // IL2CPP: Namespace::Class.Method — method is last segment after ::
            int dcolon = clean.IndexOf("::", StringComparison.Ordinal);
            int lastDot = 0;
            if (dcolon >= 0)
            {
                string afterDcolon = clean.Substring(dcolon + 2);
                lastDot = afterDcolon.LastIndexOf('.');
                return lastDot >= 0 ? afterDcolon.Substring(lastDot + 1) : afterDcolon;
            }

            // Mono: after last '.'
            lastDot = clean.LastIndexOf('.');
            return lastDot >= 0 ? clean.Substring(lastDot + 1) : clean;
        }

        MonoScript FindScript(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (m_ScriptCache.TryGetValue(name, out var cached)) return cached.script;

            string[] guids = AssetDatabase.FindAssets(string.Concat("t:MonoScript ", name));
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (Path.GetFileNameWithoutExtension(path) != name) continue;

                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                m_ScriptCache[name] = (script, script != null);
                return script;
            }

            m_ScriptCache[name] = (null, false);
            return null;
        }

        // ═══════════════════════════════════════════════════
        //  METHOD NAME PARSING
        // ═══════════════════════════════════════════════════

        static string ExtractAssembly(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int bang = raw.IndexOf('!');
            return bang > 0 ? raw.Substring(0, bang) : "";
        }

        static string StripAssembly(string raw)
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
        static string StripLeadingColons(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw ?? "";
            int bang = raw.IndexOf('!');
            if (bang < 0) return raw;
            int afterBang = bang + 1;
            if (afterBang + 1 < raw.Length && raw[afterBang] == ':' && raw[afterBang + 1] == ':')
                return string.Concat(raw.Substring(0, afterBang), raw.Substring(afterBang + 2));
            return raw;
        }

        // ═══════════════════════════════════════════════════
        //  FORMATTING — used during analysis (not per-bind)
        // ═══════════════════════════════════════════════════

        string FormatStackFrameDisplay(ResolvedFrame frame, int depth)
        {
            m_SharedSB.Clear();
            m_SharedSB.Append(depth == 0 ? "→ " : "  ");
            m_SharedSB.Append(StripAssembly(frame.RawMethodName));

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

        static string FormatTopFrame(ResolvedFrame frame)
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

        static string FormatTopFrameWithAssembly(ResolvedFrame frame)
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

        static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return string.Concat((bytes / (1024f * 1024f)).ToString("F1"), " MB");
            if (bytes >= 1024)
                return string.Concat((bytes / 1024f).ToString("F1"), " KB");
            return string.Concat(bytes.ToString(), " B");
        }

        // ═══════════════════════════════════════════════════
        //  CALLSTACK KEY — uses shared StringBuilder
        // ═══════════════════════════════════════════════════

        string BuildNormalizedCallStackKey(List<ResolvedFrame> frames)
        {
            m_SharedSB.Clear();
            for (int i = 0; i < frames.Count; i++)
            {
                AppendNormalizedKeyPart(m_SharedSB, frames[i]);
                m_SharedSB.Append('|');
            }
            return m_SharedSB.ToString();
        }

        static string NormalizeKeyPart(ResolvedFrame f)
        {
            string method = StripAssembly(f.RawMethodName);
            string file = !string.IsNullOrEmpty(f.SourceFile) ? Path.GetFileName(f.SourceFile) : "";
            return f.SourceLine > 0
                ? string.Concat(method, "@", file, ":", f.SourceLine.ToString())
                : string.Concat(method, "@", file);
        }

        static void AppendNormalizedKeyPart(StringBuilder sb, ResolvedFrame f)
        {
            sb.Append(StripAssembly(f.RawMethodName));
            sb.Append('@');
            if (!string.IsNullOrEmpty(f.SourceFile))
                sb.Append(Path.GetFileName(f.SourceFile));
            if (f.SourceLine > 0)
            {
                sb.Append(':');
                sb.Append(f.SourceLine);
            }
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

        static Label Lbl(string text, float width, FontStyle font = FontStyle.Normal, float grow = 0)
        {
            var lbl = new Label(text)
            {
                style = { unityFontStyleAndWeight = font, fontSize = 11 }
            };
            if (width > 0) lbl.style.width = width;
            if (grow > 0) lbl.style.flexGrow = grow;
            return lbl;
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

        // ═══════════════════════════════════════════════════
        //  ANALYSIS SNAPSHOT — serializable data container
        // ═══════════════════════════════════════════════════

        [Serializable]
        class AnalysisSnapshot
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
        class RawAllocation
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
        class CallsiteGroup
        {
            public string Key;
            public string DisplayName;
            public long TotalBytes;
            public int Count;
            public float Percentage;
            public List<ResolvedFrame> ResolvedCallStack;
            public List<RawAllocation> Allocations;

            // Pre-computed display strings (built once during grouping)
            public string FormattedBytes;
            public string FormattedCount;
            public string FormattedAvg;
            public string FormattedPct;
        }

        [Serializable]
        struct ResolvedFrame
        {
            public string RawMethodName;
            public string SourceFile;
            public int SourceLine;
        }

        class DepthEntry
        {
            public string Name;
            public int Remaining;
        }
    }
}
