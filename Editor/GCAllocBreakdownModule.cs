using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.Editor;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace GCAllocBreakdown.Editor
{
    [Serializable]
    [ProfilerModuleMetadata("GC Alloc")]
    public class GCAllocBreakdownModule : ProfilerModule
    {
        static readonly ProfilerCounterDescriptor[] k_ChartCounters =
        {
            new ProfilerCounterDescriptor("GC Allocated In Frame", ProfilerCategory.Memory),
            new ProfilerCounterDescriptor("GC Allocation In Frame Count", ProfilerCategory.Memory),
        };

        static readonly string[] k_AutoEnabledCategories = { ProfilerCategory.Memory.Name };

        public GCAllocBreakdownModule()
            : base(k_ChartCounters, autoEnabledCategoryNames: k_AutoEnabledCategories) { }

        public override ProfilerModuleViewController CreateDetailsViewController()
            => new GCAllocModuleDetailsView(ProfilerWindow);
    }

    /// <summary>
    /// Per-frame allocation breakdown in the Profiler module details area.
    /// MultiColumnListView with flattened group + callstack rows.
    /// Per-frame results are cached so scrubbing back to visited frames is instant.
    /// </summary>
    public class GCAllocModuleDetailsView : ProfilerModuleViewController
    {
        // ── Constants ──
        const float COL_BYTES = 80;
        const float COL_COUNT = 50;
        const float COL_AVG = 65;
        const int ROW_HEIGHT = 20;
        const int MAX_STACK_FRAMES = 20;
        const int MAX_CACHED_FRAMES = 512;

        const string k_ColSite = "site";
        const string k_ColBytes = "bytes";
        const string k_ColCount = "count";
        const string k_ColAvg = "avg";

        static readonly Color k_DimGray = new(0.7f, 0.7f, 0.7f);
        static readonly Color k_TopFrame = new(0.9f, 0.9f, 0.6f);
        static readonly Color k_CallerFrame = new(0.55f, 0.55f, 0.55f);

        // ── UI ──
        Label m_SummaryLabel;
        Label m_WarningLabel;
        MultiColumnListView m_ListView;
        string m_NameFilter = "";

        // ── Sort ──
        enum SortCol { Bytes, Count, Avg, Name }
        SortCol m_SortCol = SortCol.Bytes;
        bool m_SortAsc;

        // ── Per-frame cache: frameIndex → fully extracted + sorted data ──
        readonly Dictionary<int, CachedFrameData> m_FrameCache = new(128);
        readonly List<int> m_CacheInsertOrder = new(128);

        // ── Active state ──
        CachedFrameData m_ActiveFrame;
        readonly List<DisplayRow> m_DisplayRows = new(128);
        readonly HashSet<string> m_ExpandedKeys = new();
        int m_LastFrameIndex = -1;

        // ── DisplayRow pool ──
        readonly List<DisplayRow> m_RowPool = new(256);
        int m_PoolHighWater;

        // ── Extraction buffers (reused, never re-allocated) ──
        readonly StringBuilder m_SB = new(512);
        readonly List<ulong> m_AddrBuf = new(64);
        readonly List<ResolvedFrame> m_FrameBuf = new(64);
        readonly Dictionary<string, FrameGroup> m_GroupDict = new(64);
        readonly List<DepthEntry> m_DepthStack = new(32);

        public GCAllocModuleDetailsView(ProfilerWindow profilerWindow)
            : base(profilerWindow) { }

        protected override VisualElement CreateView()
        {
            var root = new VisualElement
            {
                style = { paddingTop = 6, paddingLeft = 6, paddingRight = 6,
                    paddingBottom = 6, flexGrow = 1,
                    backgroundColor = EditorGUIUtility.isProSkin
                        ? new Color(0.22f, 0.22f, 0.22f)
                        : new Color(0.76f, 0.76f, 0.76f) }
            };

            // Toolbar
            var toolbar = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    marginBottom = 4 }
            };
            toolbar.Add(new Button(OnOpenAnalyzer)
            {
                text = "Open GC Alloc Analyzer",
                tooltip = "Open the GC Alloc Analyzer window for multi-frame analysis."
            });
            var searchField = new ToolbarSearchField
            {
                style = { minWidth = 150, flexGrow = 1, marginLeft = 8 }
            };
            searchField.RegisterValueChangedCallback(evt =>
            {
                m_NameFilter = evt.newValue;
                m_PoolHighWater = 0;
                FlattenAndRefresh();
            });
            toolbar.Add(searchField);
            root.Add(toolbar);

            // Warning label
            m_WarningLabel = new Label
            {
                style = { color = new Color(1f, 0.7f, 0.2f), fontSize = 10,
                    marginBottom = 4, whiteSpace = WhiteSpace.Normal }
            };
            m_WarningLabel.style.display = DisplayStyle.None;
            root.Add(m_WarningLabel);

            // Summary
            m_SummaryLabel = new Label
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 12, marginBottom = 4 }
            };
            root.Add(m_SummaryLabel);

            // MultiColumnListView
            m_ListView = new MultiColumnListView
            {
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                fixedItemHeight = ROW_HEIGHT,
                selectionType = SelectionType.None,
                showBorder = true,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                sortingMode = ColumnSortingMode.Custom,
                style = { flexGrow = 1 }
            };

            m_ListView.columns.Add(new Column { name = k_ColSite, title = "Allocation Site",
                stretchable = true, minWidth = 120, sortable = true,
                makeCell = MakeSiteCell, bindCell = BindSiteCell });
            m_ListView.columns.Add(new Column { name = k_ColBytes, title = "Bytes",
                width = COL_BYTES, sortable = true, resizable = true,
                makeCell = MakeDataCell, bindCell = BindBytesCell,
                unbindCell = UnbindStyledCell });
            m_ListView.columns.Add(new Column { name = k_ColCount, title = "Count",
                width = COL_COUNT, sortable = true, resizable = true,
                makeCell = MakeDataCell, bindCell = BindCountCell });
            m_ListView.columns.Add(new Column { name = k_ColAvg, title = "Avg",
                width = COL_AVG, sortable = true, resizable = true,
                makeCell = MakeDataCell, bindCell = BindAvgCell });

            m_ListView.itemsSource = m_DisplayRows;

            // Set initial sort indicator to Bytes descending
            m_ListView.sortColumnDescriptions.Add(
                new SortColumnDescription { columnName = k_ColBytes, direction = SortDirection.Descending });
            m_ListView.columnSortingChanged += OnColumnSortingChanged;

            root.Add(m_ListView);

            ProfilerWindow.SelectedFrameIndexChanged += OnFrameChanged;
            GCAllocSettings.SettingsChanged += OnSettingsChanged;
            LoadFrame();

            return root;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ProfilerWindow.SelectedFrameIndexChanged -= OnFrameChanged;
                GCAllocSettings.SettingsChanged -= OnSettingsChanged;
            }
            base.Dispose(disposing);
        }

        void OnSettingsChanged()
        {
            m_ListView?.RefreshItems();
        }

        static void OnOpenAnalyzer() => GCAllocAnalyzerWindow.ShowWindow();

        void OnFrameChanged(long frame)
        {
            int f = (int)frame;
            if (f == m_LastFrameIndex) return;
            LoadFrame();
        }

        // ═══════════════════════════════════════════════════
        //  FRAME LOADING — cache hit = instant swap
        // ═══════════════════════════════════════════════════

        void LoadFrame()
        {
            int frameIndex = (int)ProfilerWindow.selectedFrameIndex;
            m_LastFrameIndex = frameIndex;

            if (frameIndex < 0)
            {
                m_SummaryLabel.text = "No frame selected.";
                m_ActiveFrame = null;
                m_DisplayRows.Clear();
                m_ListView.RefreshItems();
                return;
            }

            // Cache hit — just swap in cached data
            if (m_FrameCache.TryGetValue(frameIndex, out var cached))
            {
                m_ActiveFrame = cached;
            }
            else
            {
                // Cache miss — extract from Profiler
                cached = ExtractFrame(frameIndex);
                CacheFrame(frameIndex, cached);
                m_ActiveFrame = cached;
            }

            // Sort according to current sort setting
            SortGroups(m_ActiveFrame.Groups);

            // Update UI from cached data
            UpdateSummary(frameIndex);
            m_PoolHighWater = 0;
            FlattenAndRefresh();
        }

        CachedFrameData ExtractFrame(int frameIndex)
        {
            m_GroupDict.Clear();
            var groups = new List<FrameGroup>(32);
            bool anyCS = false;
            long totalBytes = 0;
            int totalCount = 0;

            for (int threadIdx = 0; threadIdx < 256; threadIdx++)
            {
                using var raw = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIdx);
                if (!raw.valid) break;

                int gcAllocId = raw.GetMarkerId("GC.Alloc");
                if (gcAllocId == FrameDataView.invalidMarkerId) continue;

                string threadName = raw.threadName;

                m_DepthStack.Clear();

                for (int i = 0; i < raw.sampleCount; i++)
                {
                    int markerId = raw.GetSampleMarkerId(i);
                    int childCount = raw.GetSampleChildrenCount(i);
                    string sampleName = raw.GetSampleName(i);

                    // Maintain depth stack
                    while (m_DepthStack.Count > 0 &&
                           m_DepthStack[m_DepthStack.Count - 1].Remaining <= 0)
                        m_DepthStack.RemoveAt(m_DepthStack.Count - 1);

                    if (m_DepthStack.Count > 0)
                        m_DepthStack[m_DepthStack.Count - 1].Remaining--;

                    // Some GC.Alloc samples carry no byte-count metadata; reading metadata[0] when count is 0 throws.
                    if (markerId == gcAllocId && raw.GetSampleMetadataCount(i) > 0)
                    {
                        long bytes = raw.GetSampleMetadataAsLong(i, 0);
                        if (bytes > 0)
                        {
                            totalBytes += bytes;
                            totalCount++;

                            m_FrameBuf.Clear();
                            m_AddrBuf.Clear();
                            raw.GetSampleCallstack(i, m_AddrBuf);

                            if (m_AddrBuf.Count > 0)
                            {
                                anyCS = true;
                                for (int a = 0; a < m_AddrBuf.Count; a++)
                                {
                                    var info = raw.ResolveMethodInfo(m_AddrBuf[a]);
                                    if (string.IsNullOrEmpty(info.methodName)) continue;
                                    m_FrameBuf.Add(new ResolvedFrame
                                    {
                                        RawMethodName = info.methodName.Trim(),
                                        SourceFile = (info.sourceFileName ?? "").Trim(),
                                        SourceLine = (int)info.sourceFileLine
                                    });
                                }
                            }

                            string key;
                            string display;
                            List<ResolvedFrame> stackCopy = null;

                            if (m_FrameBuf.Count > 0)
                            {
                                key = BuildKey(m_FrameBuf);
                                display = GCAllocUtils.FormatTopFrame(m_FrameBuf[0]);
                                stackCopy = new List<ResolvedFrame>(m_FrameBuf.Count);
                                for (int fc = 0; fc < m_FrameBuf.Count; fc++)
                                    stackCopy.Add(m_FrameBuf[fc]);
                            }
                            else
                            {
                                string parentMethod = m_DepthStack.Count > 0
                                    ? m_DepthStack[m_DepthStack.Count - 1].Name : "<root>";
                                key = string.Concat("nostack|", parentMethod);
                                display = parentMethod;
                            }

                            if (!m_GroupDict.TryGetValue(key, out var g))
                            {
                                g = new FrameGroup
                                {
                                    Key = key,
                                    DisplayName = display,
                                    CallStack = stackCopy
                                };
                                m_GroupDict[key] = g;
                                groups.Add(g);
                            }
                            g.TotalBytes += bytes;
                            g.Count++;
                        }
                    }

                    if (childCount > 0)
                        m_DepthStack.Add(new DepthEntry { Name = sampleName, Remaining = childCount });
                }
            }

            // Pre-compute all display strings
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                g.FormattedBytes = GCAllocUtils.FormatBytes(g.TotalBytes);
                g.FormattedCount = string.Concat(g.Count.ToString(), "x");
                g.FormattedAvg = GCAllocUtils.FormatBytes(g.TotalBytes / Math.Max(1, g.Count));

                if (g.CallStack != null && g.CallStack.Count > 0)
                {
                    int max = g.CallStack.Count < MAX_STACK_FRAMES
                        ? g.CallStack.Count : MAX_STACK_FRAMES;
                    g.StackDisplayStrings = new string[max];
                    for (int f = 0; f < max; f++)
                        g.StackDisplayStrings[f] = FormatStackFrame(g.CallStack[f], f);

                    if (g.CallStack.Count > MAX_STACK_FRAMES)
                    {
                        m_SB.Clear();
                        m_SB.Append("  ... +");
                        m_SB.Append(g.CallStack.Count - MAX_STACK_FRAMES);
                        m_SB.Append(" more");
                        g.OverflowText = m_SB.ToString();
                    }
                }
            }

            return new CachedFrameData
            {
                Groups = groups,
                TotalBytes = totalBytes,
                TotalCount = totalCount,
                HadCallStacks = anyCS,
                FormattedSummaryBytes = GCAllocUtils.FormatBytes(totalBytes)
            };
        }

        void CacheFrame(int frameIndex, CachedFrameData data)
        {
            m_FrameCache[frameIndex] = data;
            m_CacheInsertOrder.Add(frameIndex);

            // LRU eviction
            while (m_CacheInsertOrder.Count > MAX_CACHED_FRAMES)
            {
                int oldest = m_CacheInsertOrder[0];
                m_CacheInsertOrder.RemoveAt(0);
                m_FrameCache.Remove(oldest);
            }
        }

        void UpdateSummary(int frameIndex)
        {
            var d = m_ActiveFrame;
            m_SB.Clear();
            m_SB.Append("Frame "); m_SB.Append(GCAllocUtils.DisplayFrame(frameIndex));
            m_SB.Append(":  "); m_SB.Append(d.FormattedSummaryBytes);
            m_SB.Append("  across "); m_SB.Append(d.TotalCount);
            m_SB.Append(" alloc(s) in "); m_SB.Append(d.Groups.Count);
            m_SB.Append(" site(s)");
            m_SummaryLabel.text = m_SB.ToString();

            if (!d.HadCallStacks && d.TotalCount > 0)
            {
                m_WarningLabel.text = "⚠ Call Stacks not enabled — enable via Profiler toolbar → Call Stacks → GC.Alloc";
                m_WarningLabel.style.display = DisplayStyle.Flex;
            }
            else
                m_WarningLabel.style.display = DisplayStyle.None;
        }

        // ═══════════════════════════════════════════════════
        //  SORT — in-place, no allocations
        // ═══════════════════════════════════════════════════

        void SortGroups(List<FrameGroup> groups)
        {
            int dir = m_SortAsc ? 1 : -1;
            switch (m_SortCol)
            {
                case SortCol.Bytes:
                    groups.Sort((a, b) => dir * a.TotalBytes.CompareTo(b.TotalBytes));
                    break;
                case SortCol.Count:
                    groups.Sort((a, b) => dir * a.Count.CompareTo(b.Count));
                    break;
                case SortCol.Avg:
                    groups.Sort((a, b) =>
                    {
                        long aa = a.TotalBytes / Math.Max(1, a.Count);
                        long bb = b.TotalBytes / Math.Max(1, b.Count);
                        return dir * aa.CompareTo(bb);
                    });
                    break;
                case SortCol.Name:
                    groups.Sort((a, b) =>
                        dir * string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal));
                    break;
            }
        }

        void OnColumnSortingChanged()
        {
            using var e = m_ListView.sortedColumns.GetEnumerator();
            if (!e.MoveNext()) return;
            var desc = e.Current;

            m_SortCol = desc.columnName switch
            {
                k_ColSite  => SortCol.Name,
                k_ColBytes => SortCol.Bytes,
                k_ColCount => SortCol.Count,
                k_ColAvg   => SortCol.Avg,
                _          => m_SortCol
            };
            m_SortAsc = desc.direction == SortDirection.Ascending;

            if (m_ActiveFrame != null)
                SortGroups(m_ActiveFrame.Groups);
            m_PoolHighWater = 0;
            FlattenAndRefresh();
        }

        // ═══════════════════════════════════════════════════
        //  FLATTEN — groups + expanded stacks into pooled rows
        // ═══════════════════════════════════════════════════

        void FlattenAndRefresh()
        {
            m_DisplayRows.Clear();

            if (m_ActiveFrame == null)
            {
                m_ListView.RefreshItems();
                return;
            }

            var groups = m_ActiveFrame.Groups;
            bool hasFilter = m_NameFilter.Length > 0;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (hasFilter && g.DisplayName.IndexOf(m_NameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                bool hasStack = g.CallStack != null && g.CallStack.Count > 0;
                bool expanded = m_ExpandedKeys.Contains(g.Key);

                var headerRow = GetPooledRow();
                headerRow.Type = RowType.GroupHeader;
                headerRow.Group = g;
                headerRow.IsExpandable = hasStack;
                headerRow.IsExpanded = expanded;
                headerRow.DisplayText = null;
                headerRow.FrameDepth = 0;
                headerRow.Frame = default;
                m_DisplayRows.Add(headerRow);

                if (expanded && hasStack)
                {
                    int max = g.StackDisplayStrings != null ? g.StackDisplayStrings.Length : 0;
                    for (int f = 0; f < max; f++)
                    {
                        var stackRow = GetPooledRow();
                        stackRow.Type = RowType.StackFrame;
                        stackRow.Group = null;
                        stackRow.Frame = g.CallStack[f];
                        stackRow.FrameDepth = f;
                        stackRow.DisplayText = g.StackDisplayStrings[f];
                        stackRow.IsExpandable = false;
                        stackRow.IsExpanded = false;
                        m_DisplayRows.Add(stackRow);
                    }

                    if (g.OverflowText != null)
                    {
                        var overflowRow = GetPooledRow();
                        overflowRow.Type = RowType.StackFrame;
                        overflowRow.Group = null;
                        overflowRow.FrameDepth = -1;
                        overflowRow.DisplayText = g.OverflowText;
                        overflowRow.Frame = default;
                        overflowRow.IsExpandable = false;
                        overflowRow.IsExpanded = false;
                        m_DisplayRows.Add(overflowRow);
                    }
                }
            }

            m_ListView.RefreshItems();
        }

        DisplayRow GetPooledRow()
        {
            if (m_PoolHighWater < m_RowPool.Count)
                return m_RowPool[m_PoolHighWater++];

            var row = new DisplayRow();
            m_RowPool.Add(row);
            m_PoolHighWater++;
            return row;
        }

        // ═══════════════════════════════════════════════════
        //  MULTI-COLUMN CELLS — per-column make/bind
        // ═══════════════════════════════════════════════════

        VisualElement MakeSiteCell()
        {
            var container = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, flexGrow = 1, alignItems = Align.Center }
            };

            var toggle = new Label
            {
                name = "toggle",
                style = { width = 18, fontSize = 10, unityTextAlign = TextAnchor.MiddleCenter,
                    flexShrink = 0 }
            };
            toggle.RegisterCallback<ClickEvent>(OnToggleClicked);
            container.Add(toggle);

            container.Add(new Label { name = "text", style = { fontSize = 11,
                overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis,
                flexGrow = 1 } });

            return container;
        }

        static VisualElement MakeDataCell() => new Label { style = { fontSize = 11, flexGrow = 1, unityTextAlign = TextAnchor.MiddleLeft } };

        void BindSiteCell(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_DisplayRows.Count) return;
            var dr = m_DisplayRows[index];
            var toggleLbl = cell.Q<Label>("toggle");
            var textLbl = cell.Q<Label>("text");

            if (dr.Type == RowType.GroupHeader)
            {
                var g = dr.Group;
                toggleLbl.userData = dr.IsExpandable ? g.Key : null;
                toggleLbl.text = dr.IsExpandable ? (dr.IsExpanded ? "▼" : "▶") : " ";
                toggleLbl.style.color = StyleKeyword.Null;

                textLbl.text = g.DisplayName;
                textLbl.tooltip = g.DisplayName;
                textLbl.style.color = StyleKeyword.Null;
            }
            else
            {
                toggleLbl.userData = null;
                toggleLbl.text = "";

                textLbl.text = dr.DisplayText ?? "";
                textLbl.tooltip = dr.Frame.RawMethodName ?? "";
                textLbl.style.color = dr.FrameDepth == 0 ? k_TopFrame
                    : dr.FrameDepth > 0 ? k_CallerFrame : k_DimGray;
            }
        }

        void BindBytesCell(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_DisplayRows.Count) return;
            var dr = m_DisplayRows[index];
            var lbl = (Label)cell;

            if (dr.Type == RowType.GroupHeader)
            {
                var g = dr.Group;
                lbl.text = g.FormattedBytes;
                lbl.style.color = GCAllocSettings.ColorForBytes(g.TotalBytes);
                lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            }
            else
            {
                lbl.text = "";
            }
        }

        void BindCountCell(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_DisplayRows.Count) return;
            var dr = m_DisplayRows[index];
            ((Label)cell).text = dr.Type == RowType.GroupHeader ? dr.Group.FormattedCount : "";
        }

        void BindAvgCell(VisualElement cell, int index)
        {
            if (index < 0 || index >= m_DisplayRows.Count) return;
            var dr = m_DisplayRows[index];
            ((Label)cell).text = dr.Type == RowType.GroupHeader ? dr.Group.FormattedAvg : "";
        }

        static void UnbindStyledCell(VisualElement cell, int index)
        {
            cell.style.color = StyleKeyword.Null;
            cell.style.unityFontStyleAndWeight = StyleKeyword.Null;
        }

        void OnToggleClicked(ClickEvent evt)
        {
            var key = ((VisualElement)evt.target).userData as string;
            if (key == null) return;

            if (m_ExpandedKeys.Contains(key))
                m_ExpandedKeys.Remove(key);
            else
                m_ExpandedKeys.Add(key);

            m_PoolHighWater = 0;
            FlattenAndRefresh();
        }

        // ═══════════════════════════════════════════════════
        //  FORMATTING
        // ═══════════════════════════════════════════════════

        string FormatStackFrame(ResolvedFrame frame, int depth)
        {
            m_SB.Clear();
            m_SB.Append(depth == 0 ? "→ " : "  ");
            m_SB.Append(GCAllocUtils.StripAssembly(frame.RawMethodName));

            if (!string.IsNullOrEmpty(frame.SourceFile))
            {
                m_SB.Append("  (");
                m_SB.Append(Path.GetFileName(frame.SourceFile));
                if (frame.SourceLine > 0)
                {
                    m_SB.Append(':');
                    m_SB.Append(frame.SourceLine);
                }
                m_SB.Append(')');
            }

            return m_SB.ToString();
        }

        string BuildKey(List<ResolvedFrame> frames)
        {
            m_SB.Clear();
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                m_SB.Append(GCAllocUtils.StripAssembly(f.RawMethodName));
                m_SB.Append('@');
                if (!string.IsNullOrEmpty(f.SourceFile))
                    m_SB.Append(Path.GetFileName(f.SourceFile));
                if (f.SourceLine > 0) { m_SB.Append(':'); m_SB.Append(f.SourceLine); }
                m_SB.Append('|');
            }
            return m_SB.ToString();
        }

        // ═══════════════════════════════════════════════════
        //  MODULE-SPECIFIC DATA STRUCTURES
        // ═══════════════════════════════════════════════════

        class FrameGroup
        {
            public string Key;
            public string DisplayName;
            public long TotalBytes;
            public int Count;
            public List<ResolvedFrame> CallStack;

            // Pre-computed display strings (built once during extraction)
            public string FormattedBytes;
            public string FormattedCount;
            public string FormattedAvg;
            public string[] StackDisplayStrings;
            public string OverflowText;
        }

        class CachedFrameData
        {
            public List<FrameGroup> Groups;
            public long TotalBytes;
            public int TotalCount;
            public bool HadCallStacks;
            public string FormattedSummaryBytes;
        }

        enum RowType { GroupHeader, StackFrame }

        class DisplayRow
        {
            public RowType Type;
            public FrameGroup Group;
            public ResolvedFrame Frame;
            public int FrameDepth;
            public string DisplayText;
            public bool IsExpandable;
            public bool IsExpanded;
        }
    }
}
