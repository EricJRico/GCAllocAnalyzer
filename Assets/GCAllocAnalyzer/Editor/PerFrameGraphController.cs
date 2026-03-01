using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace GCAllocBreakdown.Editor
{
    // ═══════════════════════════════════════════════════
    //  PER-FRAME GRAPH CONTROLLER
    //  Manages the per-frame bar graph using a custom-drawn
    //  GraphElement instead of one VisualElement per bar.
    //  The main window creates an instance and delegates
    //  all graph operations to it.
    // ═══════════════════════════════════════════════════

    internal class PerFrameGraphController
    {
        // ═══════════════════════════════════════════════════
        //  CONSTANTS
        // ═══════════════════════════════════════════════════

        const float k_GraphHeight = 120f;
        const float k_GraphYAxisWidth = 52f;
        const float k_GraphXAxisHeight = 16f;
        const int k_InitialBarCapacity = 512;
        const int k_InitialGridLineCapacity = 8;
        const int k_MaxGridLineLabels = 16;
        const long k_MinGridStep = 1024; // 1 KB

        // ═══════════════════════════════════════════════════
        //  COLORS
        // ═══════════════════════════════════════════════════

        static readonly Color k_DimGray = new(0.7f, 0.7f, 0.7f);

        // ═══════════════════════════════════════════════════
        //  CALLBACKS
        // ═══════════════════════════════════════════════════

        readonly Action<int> m_OnFrameSelected;
        readonly Func<int> m_GetSelectedMarkerIndex;

        /// <summary>Invoked when the user drag-selects a range of bars. Args: startFrame, endFrame.</summary>
        public event Action<int, int> OnSelectionChanged;

        /// <summary>Invoked when the user completes a drag-select. Args: startFrame, endFrame.</summary>
        public event Action<int, int> OnDragCompleted;

        /// <summary>Invoked when the user requests re-analysis (e.g. via context menu).</summary>
        public event Action OnAnalyzeRequested;

        /// <summary>Invoked when the user clicks the Reset button to restore full range.</summary>
        public event Action OnResetRequested;

        // ═══════════════════════════════════════════════════
        //  DATA REFERENCES
        // ═══════════════════════════════════════════════════

        AnalysisSnapshot m_Snapshot;
        List<CallsiteGroup> m_FilteredGroups;
        GraphFrameStore m_FrameStore;
        int m_AnalyzedFrameStart = -1;
        int m_AnalyzedFrameEnd = -1;

        // ═══════════════════════════════════════════════════
        //  BAR COMPUTATION BUFFERS
        // ═══════════════════════════════════════════════════

        BarData[] m_Bars;
        BarData[] m_OverlayBars;
        BarData[] m_SortScratch; // scratch buffer for rearranging bars during sort
        int m_BarCount;
        int m_OverlayBarCount;
        GridLine[] m_GridLines;
        int m_GridLineCount;

        // Bucketing state
        int m_FramesPerBucket;
        long m_YAxisMax;

        // Last known selection bar indices (for context menu access)
        int m_LastSelectionStartBar = -1;
        int m_LastSelectionEndBar = -1;

        // Cached tooltip bar index to avoid per-move string allocations
        int m_LastTooltipBar = -1;

        // Per-frame scratch buffer
        long[] m_PerFrameBuffer;

        // ═══════════════════════════════════════════════════
        //  SORTING
        // ═══════════════════════════════════════════════════

        bool m_OrderByMagnitude;
        int[] m_SortedBarIndices; // maps display position -> original bar index

        /// <summary>
        /// When true, bars are sorted by magnitude (descending) instead of frame order.
        /// </summary>
        public bool OrderByMagnitude
        {
            get => m_OrderByMagnitude;
            set
            {
                if (m_OrderByMagnitude == value) return;
                m_OrderByMagnitude = value;
                RebuildGraph();
            }
        }

        // ═══════════════════════════════════════════════════
        //  UI ELEMENTS
        // ═══════════════════════════════════════════════════

        Foldout m_GraphFoldout;
        VisualElement m_GraphRoot;
        GraphElement m_GraphElement;
        Label m_GraphYMax, m_GraphYMid;
        Label m_GraphXStart, m_GraphXEnd;
        Label m_GraphOverlayLabel;
        Button m_SortToggleBtn;
        Button m_ResetBtn;

        // Grid line labels (absolutely positioned over the graph)
        Label[] m_GridLineLabels;
        int m_GridLineLabelCount;

        // ═══════════════════════════════════════════════════
        //  PUBLIC API
        // ═══════════════════════════════════════════════════

        /// <summary>The root Foldout element -- add this to the parent layout.</summary>
        public VisualElement Root => m_GraphFoldout;

        public PerFrameGraphController(Action<int> onFrameSelected, Func<int> getSelectedMarkerIndex)
        {
            m_OnFrameSelected = onFrameSelected;
            m_GetSelectedMarkerIndex = getSelectedMarkerIndex;

            m_Bars = new BarData[k_InitialBarCapacity];
            m_OverlayBars = new BarData[k_InitialBarCapacity];
            m_GridLines = new GridLine[k_InitialGridLineCapacity];
            m_GridLineLabels = new Label[k_MaxGridLineLabels];

            BuildUI();
        }

        /// <summary>
        /// Store data references after each analysis run or Pull Data.
        /// </summary>
        public void SetData(GraphFrameStore frameStore, AnalysisSnapshot snapshot, List<CallsiteGroup> filteredGroups)
        {
            m_FrameStore = frameStore;
            m_Snapshot = snapshot;
            m_FilteredGroups = filteredGroups;

            // Determine analyzed range (if snapshot has data)
            if (snapshot != null && snapshot.HasData)
            {
                m_AnalyzedFrameStart = snapshot.FrameStart;
                m_AnalyzedFrameEnd = snapshot.FrameEnd;
            }
            else
            {
                m_AnalyzedFrameStart = -1;
                m_AnalyzedFrameEnd = -1;
            }
        }

        /// <summary>
        /// Rebuild the entire graph from current frame store data.
        /// </summary>
        public void RebuildGraph()
        {
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData)
            {
                m_GraphFoldout.style.display = DisplayStyle.None;
                return;
            }

            var perFrame = m_FrameStore.FullFrameBytes;
            int frameCount = perFrame.Length;

            m_GraphFoldout.style.display = DisplayStyle.Flex;

            float areaWidth = m_GraphElement.contentRect.width;
            if (float.IsNaN(areaWidth) || areaWidth < 1f) areaWidth = 400f;

            // ── Bucketing ──
            m_FramesPerBucket = 1;
            if (frameCount > (int)areaWidth)
                m_FramesPerBucket = Mathf.CeilToInt((float)frameCount / Mathf.Max(1f, areaWidth));

            int bucketCount = Mathf.CeilToInt((float)frameCount / m_FramesPerBucket);

            // ── Ensure bar buffer capacity ──
            EnsureBarCapacity(ref m_Bars, bucketCount);
            m_BarCount = bucketCount;

            // ── Fill buckets and find max ──
            long maxValue = 0;
            for (int b = 0; b < bucketCount; b++)
            {
                int startIdx = b * m_FramesPerBucket;
                int endIdx = Mathf.Min(startIdx + m_FramesPerBucket, frameCount);
                long bucketMax = 0;
                for (int i = startIdx; i < endIdx; i++)
                {
                    if (perFrame[i] > bucketMax) bucketMax = perFrame[i];
                }

                int frameStart = m_FrameStore.FullFrameStart + startIdx;
                int frameEnd = m_FrameStore.FullFrameStart + endIdx - 1;

                m_Bars[b] = new BarData
                {
                    Value = bucketMax,
                    StartFrame = frameStart,
                    EndFrame = frameEnd
                };

                if (bucketMax > maxValue) maxValue = bucketMax;
            }

            m_YAxisMax = maxValue;

            // ── Compute X/W/Height for each bar ──
            float barWidth = areaWidth / bucketCount;
            for (int b = 0; b < bucketCount; b++)
            {
                m_Bars[b].X = b * barWidth;
                m_Bars[b].W = Mathf.Max(barWidth, 1f);
                m_Bars[b].Height = maxValue > 0
                    ? (float)m_Bars[b].Value / maxValue * k_GraphHeight
                    : 0f;
            }

            // ── Sorted view (order by magnitude) ──
            if (m_OrderByMagnitude)
                ApplySortedOrder(barWidth);
            else
                m_SortedBarIndices = null;

            // ── Grid lines ──
            ComputeGridLines(maxValue, k_GraphHeight);

            // ── Compute analyzed bar range for dimming ──
            int analyzedStartBar = -1, analyzedEndBar = -1;
            if (m_AnalyzedFrameStart >= 0 && m_AnalyzedFrameEnd >= 0)
            {
                ComputeAnalyzedBarRange(out analyzedStartBar, out analyzedEndBar);
            }

            // ── Push data to GraphElement ──
            m_GraphElement.YAxisMax = maxValue;
            m_GraphElement.SetBarData(m_Bars, m_BarCount);
            m_GraphElement.SetGridLines(m_GridLines, m_GridLineCount);
            m_GraphElement.SetAnalyzedRange(analyzedStartBar, analyzedEndBar);
            m_GraphElement.HasData = true;

            // ── Update axis labels ──
            m_GraphYMax.text = GCAllocUtils.FormatBytes(maxValue);
            m_GraphYMid.text = GCAllocUtils.FormatBytes(maxValue / 2);
            m_GraphXStart.text = GCAllocUtils.DisplayFrame(m_FrameStore.FullFrameStart).ToString();
            m_GraphXEnd.text = GCAllocUtils.DisplayFrame(m_FrameStore.FullFrameEnd).ToString();

            // ── Position grid line labels ──
            PositionGridLineLabels();

            // ── Reset tooltip cache (stale after rebuild) ──
            m_LastTooltipBar = -1;

            // ── Clear overlay bar count (stale data) ──
            m_OverlayBarCount = 0;
            m_GraphElement.SetOverlayData(m_OverlayBars, 0);

            // ── Re-apply overlay for currently selected marker ──
            int selectedIdx = m_GetSelectedMarkerIndex != null ? m_GetSelectedMarkerIndex() : -1;
            if (m_FilteredGroups != null &&
                selectedIdx >= 0 &&
                selectedIdx < m_FilteredGroups.Count)
                UpdateOverlay(m_FilteredGroups[selectedIdx]);
            else
                m_GraphOverlayLabel.text = "";

            // ── Show/hide reset button ──
            UpdateResetButtonVisibility();
        }

        /// <summary>
        /// Show overlay bars for a specific callsite group.
        /// </summary>
        public void UpdateOverlay(CallsiteGroup group)
        {
            if (group == null)
            {
                ClearOverlay();
                return;
            }

            if (m_Snapshot == null || m_Snapshot.PerFrameBytes == null) return;
            if (m_BarCount == 0) return;
            if (m_FrameStore == null) return;

            int snapshotFrameCount = m_Snapshot.PerFrameBytes.Length;

            // ── Build per-frame bytes for this group (within analyzed range) ──
            EnsurePerFrameBuffer(snapshotFrameCount);
            for (int i = 0; i < group.Allocations.Count; i++)
            {
                var alloc = group.Allocations[i];
                int idx = alloc.FrameIndex - m_Snapshot.FrameStart;
                if (idx >= 0 && idx < snapshotFrameCount)
                    m_PerFrameBuffer[idx] += alloc.Bytes;
            }

            // ── Bucket the group's per-frame data over the full range ──
            int fullFrameCount = m_FrameStore.FullFrameBytes.Length;
            EnsureBarCapacity(ref m_OverlayBars, m_BarCount);
            m_OverlayBarCount = m_BarCount;

            for (int b = 0; b < m_BarCount; b++)
            {
                int origBucket = m_SortedBarIndices != null ? m_SortedBarIndices[b] : b;
                int startIdx = origBucket * m_FramesPerBucket;
                int endIdx = Mathf.Min(startIdx + m_FramesPerBucket, fullFrameCount);

                long bucketMax = 0;
                for (int i = startIdx; i < endIdx; i++)
                {
                    // Map full-range index to snapshot-relative index
                    int snapshotIdx = (m_FrameStore.FullFrameStart + i) - m_Snapshot.FrameStart;
                    if (snapshotIdx >= 0 && snapshotIdx < snapshotFrameCount)
                    {
                        if (m_PerFrameBuffer[snapshotIdx] > bucketMax)
                            bucketMax = m_PerFrameBuffer[snapshotIdx];
                    }
                }

                m_OverlayBars[b] = new BarData
                {
                    X = m_Bars[b].X,
                    W = m_Bars[b].W,
                    Height = m_YAxisMax > 0
                        ? (float)bucketMax / m_YAxisMax * k_GraphHeight
                        : 0f,
                    StartFrame = m_Bars[b].StartFrame,
                    EndFrame = m_Bars[b].EndFrame,
                    Value = bucketMax
                };
            }

            // ── Push overlay to GraphElement ──
            m_GraphElement.SetOverlayData(m_OverlayBars, m_OverlayBarCount);

            // ── Update overlay label ──
            m_GraphOverlayLabel.text = group.DisplayName;
        }

        /// <summary>
        /// Clear the visual selection and highlighted bar.
        /// </summary>
        public void ClearSelection()
        {
            m_GraphElement.SetSelection(-1, -1);
            m_GraphElement.SetHighlightedBar(-1);
            m_LastSelectionStartBar = -1;
            m_LastSelectionEndBar = -1;
        }

        /// <summary>
        /// Clear all overlay bars and the overlay label.
        /// </summary>
        public void ClearOverlay()
        {
            m_OverlayBarCount = 0;
            m_GraphElement.SetOverlayData(m_OverlayBars, 0);
            m_GraphOverlayLabel.text = "";
        }

        // ═══════════════════════════════════════════════════
        //  UI CONSTRUCTION
        // ═══════════════════════════════════════════════════

        void BuildUI()
        {
            m_GraphFoldout = MakeSectionFoldout("Per-Frame Graph");
            m_GraphFoldout.style.marginLeft = 4;
            m_GraphFoldout.style.marginRight = 4;
            m_GraphFoldout.style.marginTop = 0;
            m_GraphFoldout.style.display = DisplayStyle.None; // hidden until data

            m_GraphRoot = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    height = k_GraphHeight + k_GraphXAxisHeight
                }
            };

            // ── Y-axis labels (left column) ──
            var yAxis = new VisualElement
            {
                style =
                {
                    width = k_GraphYAxisWidth,
                    justifyContent = Justify.SpaceBetween,
                    alignItems = Align.FlexEnd,
                    paddingRight = 4,
                    height = k_GraphHeight
                }
            };
            m_GraphYMax = new Label("\u2014")
            {
                style = { fontSize = 10, color = k_DimGray, unityTextAlign = TextAnchor.MiddleRight }
            };
            m_GraphYMid = new Label("\u2014")
            {
                style = { fontSize = 10, color = k_DimGray, unityTextAlign = TextAnchor.MiddleRight }
            };
            var yZero = new Label("0")
            {
                style = { fontSize = 10, color = k_DimGray, unityTextAlign = TextAnchor.MiddleRight }
            };
            yAxis.Add(m_GraphYMax);
            yAxis.Add(m_GraphYMid);
            yAxis.Add(yZero);
            m_GraphRoot.Add(yAxis);

            // ── Chart area (right side, fills remaining width) ──
            var chartColumn = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };

            // ── GraphElement — custom-drawn bars ──
            m_GraphElement = new GraphElement
            {
                style =
                {
                    height = k_GraphHeight,
                    flexGrow = 1,
                    flexShrink = 1
                }
            };
            m_GraphElement.RegisterCallback<GeometryChangedEvent>(OnGraphGeometryChanged);
            m_GraphElement.RegisterCallback<PointerMoveEvent>(OnGraphPointerMove);
            m_GraphElement.AddManipulator(new ContextualMenuManipulator(OnGraphContextMenu));

            // Subscribe to GraphElement events
            m_GraphElement.BarClicked += OnBarClicked;
            m_GraphElement.SelectionChanged += OnSelectionChangedInternal;
            m_GraphElement.DragCompleted += OnDragCompletedInternal;

            chartColumn.Add(m_GraphElement);

            // ── Reset button (inline, only visible during sub-range) ──
            m_ResetBtn = new Button(() => OnResetRequested?.Invoke())
            {
                text = "Reset to Full Range",
                tooltip = "Restore full-range analysis from cache (instant)",
                style =
                {
                    fontSize = 10,
                    paddingLeft = 6,
                    paddingRight = 6,
                    paddingTop = 2,
                    paddingBottom = 2,
                    position = Position.Absolute,
                    top = 4,
                    right = 4,
                    display = DisplayStyle.None
                }
            };
            m_GraphElement.Add(m_ResetBtn);

            // ── X-axis labels row ──
            var xAxis = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    justifyContent = Justify.SpaceBetween,
                    height = k_GraphXAxisHeight
                }
            };
            m_GraphXStart = new Label("\u2014")
            {
                style = { fontSize = 10, color = k_DimGray, flexShrink = 0 }
            };
            m_GraphOverlayLabel = new Label("")
            {
                style =
                {
                    flexGrow = 1,
                    flexShrink = 1,
                    fontSize = 10,
                    color = k_DimGray,
                    unityTextAlign = TextAnchor.MiddleCenter,
                    overflow = Overflow.Hidden,
                    textOverflow = TextOverflow.Ellipsis,
                    whiteSpace = WhiteSpace.NoWrap,
                    marginLeft = 8,
                    marginRight = 8
                }
            };
            m_SortToggleBtn = new Button(OnSortToggleClicked)
            {
                text = "Order by Size",
                tooltip = "Toggle between frame order and order by allocation size (descending)",
                style =
                {
                    fontSize = 10,
                    paddingLeft = 4,
                    paddingRight = 4,
                    paddingTop = 0,
                    paddingBottom = 0,
                    height = k_GraphXAxisHeight,
                    flexShrink = 0,
                    marginRight = 4
                }
            };

            m_GraphXEnd = new Label("\u2014")
            {
                style = { fontSize = 10, color = k_DimGray, flexShrink = 0 }
            };
            xAxis.Add(m_GraphXStart);
            xAxis.Add(m_GraphOverlayLabel);
            xAxis.Add(m_SortToggleBtn);
            xAxis.Add(m_GraphXEnd);
            chartColumn.Add(xAxis);

            m_GraphRoot.Add(chartColumn);
            m_GraphFoldout.Add(m_GraphRoot);
        }

        // ═══════════════════════════════════════════════════
        //  EVENT HANDLERS
        // ═══════════════════════════════════════════════════

        void OnGraphGeometryChanged(GeometryChangedEvent evt)
        {
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData) return;
            float oldW = evt.oldRect.width;
            float newW = evt.newRect.width;
            if (Mathf.Abs(newW - oldW) < 2f) return;
            RebuildGraph();
        }

        void OnBarClicked(int barIndex)
        {
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData) return;
            var perFrame = m_FrameStore.FullFrameBytes;
            if (barIndex < 0 || barIndex >= m_BarCount) return;

            // Resolve the actual bar index if sorted
            int resolvedIndex = m_SortedBarIndices != null ? m_SortedBarIndices[barIndex] : barIndex;

            // Find the frame with max allocation in this bucket
            int startIdx = resolvedIndex * m_FramesPerBucket;
            int endIdx = Mathf.Min(startIdx + m_FramesPerBucket, perFrame.Length);

            int maxIdx = startIdx;
            long maxVal = 0;
            for (int i = startIdx; i < endIdx; i++)
            {
                if (perFrame[i] > maxVal) { maxVal = perFrame[i]; maxIdx = i; }
            }

            int frameIndex = m_FrameStore.FullFrameStart + maxIdx;

            if (m_OnFrameSelected != null)
                m_OnFrameSelected(frameIndex);
        }

        void OnSelectionChangedInternal(int startBar, int endBar)
        {
            m_LastSelectionStartBar = startBar;
            m_LastSelectionEndBar = endBar;
            if (OnSelectionChanged == null) return;
            MapBarRangeToFrameRange(startBar, endBar, out int startFrame, out int endFrame);
            OnSelectionChanged(startFrame, endFrame);
        }

        void OnDragCompletedInternal(int startBar, int endBar)
        {
            m_LastSelectionStartBar = startBar;
            m_LastSelectionEndBar = endBar;
            if (OnDragCompleted == null) return;
            MapBarRangeToFrameRange(startBar, endBar, out int startFrame, out int endFrame);
            OnDragCompleted(startFrame, endFrame);
        }

        void OnGraphPointerMove(PointerMoveEvent evt)
        {
            if (m_BarCount == 0 || m_FrameStore == null)
            {
                m_LastTooltipBar = -1;
                m_GraphElement.tooltip = "";
                return;
            }

            float localX = evt.localPosition.x;
            int barIndex = FindBarAtX(localX);
            if (barIndex < 0 || barIndex >= m_BarCount)
            {
                m_LastTooltipBar = -1;
                m_GraphElement.tooltip = "";
                return;
            }

            // Skip recomputing the tooltip if still hovering the same bar
            if (barIndex == m_LastTooltipBar) return;
            m_LastTooltipBar = barIndex;

            // Bars are always in display order, read directly
            long val = m_Bars[barIndex].Value;
            int frameStart = m_Bars[barIndex].StartFrame;
            int frameEnd = m_Bars[barIndex].EndFrame;

            if (m_FramesPerBucket == 1)
            {
                m_GraphElement.tooltip = string.Concat(
                    "Frame ", GCAllocUtils.DisplayFrame(frameStart).ToString(),
                    ": ", GCAllocUtils.FormatBytes(val));
            }
            else
            {
                m_GraphElement.tooltip = string.Concat(
                    "Frames ", GCAllocUtils.DisplayFrame(frameStart).ToString(),
                    "\u2013", GCAllocUtils.DisplayFrame(frameEnd).ToString(),
                    ": ", GCAllocUtils.FormatBytes(val), " (max)");
            }
        }

        // ═══════════════════════════════════════════════════
        //  SORTED VIEW TOGGLE
        // ═══════════════════════════════════════════════════

        void OnSortToggleClicked()
        {
            OrderByMagnitude = !OrderByMagnitude;
            UpdateSortToggleLabel();
        }

        void UpdateSortToggleLabel()
        {
            if (m_SortToggleBtn == null) return;
            m_SortToggleBtn.text = m_OrderByMagnitude ? "Order by Frame" : "Order by Size";
        }

        // ═══════════════════════════════════════════════════
        //  CONTEXT MENU
        // ═══════════════════════════════════════════════════

        void OnGraphContextMenu(ContextualMenuPopulateEvent evt)
        {
            bool hasData = m_GraphElement != null && m_BarCount > 0;

            int selStart = m_LastSelectionStartBar;
            int selEnd = m_LastSelectionEndBar;
            bool hasActiveSelection = selStart >= 0 && selEnd >= 0;

            evt.menu.AppendAction("Select All", _ =>
            {
                m_GraphElement.SetSelection(0, m_BarCount - 1);
                m_LastSelectionStartBar = 0;
                m_LastSelectionEndBar = m_BarCount - 1;
                NotifySelectionChanged(0, m_BarCount - 1);
            },
            hasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendAction("Clear Selection", _ =>
            {
                m_GraphElement.SetSelection(-1, -1);
                m_GraphElement.SetHighlightedBar(-1);
                m_LastSelectionStartBar = -1;
                m_LastSelectionEndBar = -1;
            },
            hasActiveSelection ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendSeparator();

            evt.menu.AppendAction("Select Frame with Max GC", _ => SelectExtremeFrame(true),
                hasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendAction("Analyze Selection", _ =>
            {
                if (!hasActiveSelection) return;
                MapBarRangeToFrameRange(selStart, selEnd, out int rangeStart, out int rangeEnd);
                if (OnDragCompleted != null)
                    OnDragCompleted(rangeStart, rangeEnd);
                if (OnAnalyzeRequested != null)
                    OnAnalyzeRequested();
            },
            hasActiveSelection ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendSeparator();

            evt.menu.AppendAction(
                m_OrderByMagnitude ? "Order by Frame" : "Order by Size",
                _ => OnSortToggleClicked(),
                hasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        void SelectExtremeFrame(bool selectMax)
        {
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData) return;

            var perFrame = m_FrameStore.FullFrameBytes;
            int extremeIdx = 0;
            long extremeVal = perFrame[0];

            for (int i = 1; i < perFrame.Length; i++)
            {
                if (selectMax ? perFrame[i] > extremeVal : perFrame[i] < extremeVal)
                {
                    extremeVal = perFrame[i];
                    extremeIdx = i;
                }
            }

            // Find which bar contains this frame
            int barIdx = extremeIdx / m_FramesPerBucket;
            if (barIdx >= m_BarCount) barIdx = m_BarCount - 1;

            // If sorted, we need the display position, not the original bar index
            if (m_SortedBarIndices != null)
            {
                for (int i = 0; i < m_BarCount; i++)
                {
                    if (m_SortedBarIndices[i] == barIdx)
                    {
                        barIdx = i;
                        break;
                    }
                }
            }

            m_GraphElement.SetSelection(barIdx, barIdx);
            m_GraphElement.SetHighlightedBar(barIdx);
            m_LastSelectionStartBar = barIdx;
            m_LastSelectionEndBar = barIdx;

            int frameIndex = m_FrameStore.FullFrameStart + extremeIdx;
            if (m_OnFrameSelected != null)
                m_OnFrameSelected(frameIndex);
        }

        void NotifySelectionChanged(int startBar, int endBar)
        {
            if (OnSelectionChanged == null) return;
            MapBarRangeToFrameRange(startBar, endBar, out int startFrame, out int endFrame);
            OnSelectionChanged(startFrame, endFrame);
        }

        // ═══════════════════════════════════════════════════
        //  GRID LINE COMPUTATION
        // ═══════════════════════════════════════════════════

        void ComputeGridLines(long yMax, float graphHeight)
        {
            m_GridLineCount = 0;
            if (yMax <= 0) return;

            // Find a nice step: 1 KB, 2 KB, 4 KB, 8 KB, ... up to the max
            long step = k_MinGridStep; // 1 KB
            while (step * 5 < yMax && step < yMax)
                step *= 2;
            // If step is too large (only 1-2 lines), halve it
            while (yMax / step < 2 && step > k_MinGridStep)
                step /= 2;

            for (long v = step; v < yMax; v += step)
            {
                EnsureGridLineCapacity();
                float y = (float)v / yMax * graphHeight;
                m_GridLines[m_GridLineCount++] = new GridLine
                {
                    Y = y,
                    Label = GCAllocUtils.FormatBytes(v)
                };
            }
        }

        void PositionGridLineLabels()
        {
            // Hide all existing labels first
            for (int i = 0; i < m_GridLineLabelCount; i++)
            {
                if (m_GridLineLabels[i] != null)
                    m_GridLineLabels[i].style.display = DisplayStyle.None;
            }

            // Position labels for current grid lines
            for (int i = 0; i < m_GridLineCount && i < k_MaxGridLineLabels; i++)
            {
                Label label = EnsureGridLineLabel(i);
                label.text = m_GridLines[i].Label;
                label.style.display = DisplayStyle.Flex;
                // Position from bottom of the graph element
                label.style.position = Position.Absolute;
                label.style.bottom = m_GridLines[i].Y - 6; // center the 12px label on the line
                label.style.right = 4;
            }

            m_GridLineLabelCount = Mathf.Min(m_GridLineCount, k_MaxGridLineLabels);
        }

        // ═══════════════════════════════════════════════════
        //  SORTING
        // ═══════════════════════════════════════════════════

        void ApplySortedOrder(float barWidth)
        {
            // Build index array: maps display position -> original bucket index
            if (m_SortedBarIndices == null || m_SortedBarIndices.Length < m_BarCount)
                m_SortedBarIndices = new int[m_BarCount];

            for (int i = 0; i < m_BarCount; i++)
                m_SortedBarIndices[i] = i;

            // Sort descending by value (simple insertion sort to avoid LINQ/allocations)
            for (int i = 1; i < m_BarCount; i++)
            {
                int key = m_SortedBarIndices[i];
                long keyVal = m_Bars[key].Value;
                int j = i - 1;
                while (j >= 0 && m_Bars[m_SortedBarIndices[j]].Value < keyVal)
                {
                    m_SortedBarIndices[j + 1] = m_SortedBarIndices[j];
                    j--;
                }
                m_SortedBarIndices[j + 1] = key;
            }

            // Rearrange bars into display order so array index == display position.
            // This ensures GraphElement's selection (which uses display indices) lines up.
            EnsureBarCapacity(ref m_SortScratch, m_BarCount);
            for (int displayIdx = 0; displayIdx < m_BarCount; displayIdx++)
            {
                int origIdx = m_SortedBarIndices[displayIdx];
                m_SortScratch[displayIdx] = m_Bars[origIdx];
                m_SortScratch[displayIdx].X = displayIdx * barWidth;
                m_SortScratch[displayIdx].W = Mathf.Max(barWidth, 1f);
            }

            // Swap buffers so m_Bars is now in display order
            var tmp = m_Bars;
            m_Bars = m_SortScratch;
            m_SortScratch = tmp;
        }

        // ═══════════════════════════════════════════════════
        //  PRIVATE HELPERS
        // ═══════════════════════════════════════════════════

        void UpdateResetButtonVisibility()
        {
            if (m_ResetBtn == null || m_FrameStore == null) return;

            bool isSubRange = m_AnalyzedFrameStart >= 0
                && m_AnalyzedFrameEnd >= 0
                && m_FrameStore.HasCachedAnalysis
                && (m_AnalyzedFrameStart != m_FrameStore.FullFrameStart
                    || m_AnalyzedFrameEnd != m_FrameStore.FullFrameEnd);

            m_ResetBtn.style.display = isSubRange ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void ComputeAnalyzedBarRange(out int startBar, out int endBar)
        {
            startBar = -1;
            endBar = -1;
            if (m_FrameStore == null || m_BarCount == 0) return;
            if (m_AnalyzedFrameStart < 0 || m_AnalyzedFrameEnd < 0) return;

            // In sorted mode, analyzed bars may not be contiguous.
            // For simplicity, in sorted mode we mark all as analyzed when it's the full range,
            // otherwise mark all as analyzed (dimming refinement deferred to Task 12).
            if (m_SortedBarIndices != null)
            {
                if (m_AnalyzedFrameStart == m_FrameStore.FullFrameStart &&
                    m_AnalyzedFrameEnd == m_FrameStore.FullFrameEnd)
                {
                    startBar = 0;
                    endBar = m_BarCount - 1;
                }
                else
                {
                    startBar = 0;
                    endBar = m_BarCount - 1;
                }
                return;
            }

            // Find the first and last bars that overlap with the analyzed frame range
            for (int i = 0; i < m_BarCount; i++)
            {
                int barFrameStart = m_FrameStore.FullFrameStart + i * m_FramesPerBucket;
                int barFrameEnd = m_FrameStore.FullFrameStart + Mathf.Min((i + 1) * m_FramesPerBucket, m_FrameStore.FullFrameBytes.Length) - 1;

                bool overlaps = barFrameStart <= m_AnalyzedFrameEnd && barFrameEnd >= m_AnalyzedFrameStart;
                if (overlaps)
                {
                    if (startBar < 0) startBar = i;
                    endBar = i;
                }
            }
        }

        void EnsurePerFrameBuffer(int frameCount)
        {
            if (m_PerFrameBuffer == null || m_PerFrameBuffer.Length < frameCount)
                m_PerFrameBuffer = new long[frameCount];
            else
                Array.Clear(m_PerFrameBuffer, 0, frameCount);
        }

        static void EnsureBarCapacity(ref BarData[] array, int needed)
        {
            if (array == null || array.Length < needed)
            {
                int newSize = Mathf.Max(needed, 64);
                // Grow by doubling to reduce future allocations
                if (array != null && array.Length * 2 > newSize)
                    newSize = array.Length * 2;
                array = new BarData[newSize];
            }
        }

        void EnsureGridLineCapacity()
        {
            if (m_GridLineCount >= m_GridLines.Length)
            {
                var newArray = new GridLine[m_GridLines.Length * 2];
                Array.Copy(m_GridLines, newArray, m_GridLines.Length);
                m_GridLines = newArray;
            }
        }

        Label EnsureGridLineLabel(int index)
        {
            if (m_GridLineLabels[index] != null) return m_GridLineLabels[index];

            var label = new Label
            {
                style =
                {
                    fontSize = 9,
                    color = k_DimGray,
                    position = Position.Absolute,
                    unityTextAlign = TextAnchor.MiddleRight
                },
                pickingMode = PickingMode.Ignore
            };
            m_GridLineLabels[index] = label;
            m_GraphElement.Add(label);
            return label;
        }

        int FindBarAtX(float localX)
        {
            if (m_BarCount == 0) return -1;
            float areaWidth = m_GraphElement.contentRect.width;
            if (float.IsNaN(areaWidth) || areaWidth < 1f) return -1;

            float barWidth = areaWidth / m_BarCount;
            if (barWidth < 0.001f) return -1;

            int index = (int)(localX / barWidth);
            if (index < 0) return -1;
            if (index >= m_BarCount) return -1;
            return index;
        }

        void MapBarRangeToFrameRange(int startBar, int endBar, out int startFrame, out int endFrame)
        {
            // Ensure correct ordering
            if (startBar > endBar)
            {
                int tmp = startBar;
                startBar = endBar;
                endBar = tmp;
            }

            // Clamp
            if (startBar < 0) startBar = 0;
            if (endBar >= m_BarCount) endBar = m_BarCount - 1;

            // Bars are always in display order. In sorted mode, a range of
            // display positions may span non-contiguous frames, so find min/max.
            if (m_SortedBarIndices != null)
            {
                int minFrame = int.MaxValue;
                int maxFrame = int.MinValue;
                for (int i = startBar; i <= endBar; i++)
                {
                    if (m_Bars[i].StartFrame < minFrame) minFrame = m_Bars[i].StartFrame;
                    if (m_Bars[i].EndFrame > maxFrame) maxFrame = m_Bars[i].EndFrame;
                }
                startFrame = minFrame;
                endFrame = maxFrame;
            }
            else
            {
                startFrame = m_Bars[startBar].StartFrame;
                endFrame = m_Bars[endBar].EndFrame;
            }
        }

        // ═══════════════════════════════════════════════════
        //  SECTION FOLDOUT HELPER
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
    }
}
