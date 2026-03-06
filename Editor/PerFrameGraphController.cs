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

        // Viewport state: normalized range [0,1] over the full bar set
        float m_ViewportStart;
        float m_ViewportEnd = 1f;
        const float k_MinVisibleBars = 10f;
        int m_ViewportStartBucket; // first bucket index visible in the viewport
        int m_TotalBucketCount;    // total buckets before viewport clipping

        // Last known selection bar indices (for context menu access)
        int m_LastSelectionStartBar = -1;
        int m_LastSelectionEndBar = -1;

        // Selection stored as frame indices so it survives viewport changes
        int m_SelectionFrameStart = -1;
        int m_SelectionFrameEnd = -1;
        int m_HighlightedFrame = -1;

        // Reusable buffer marking which frames are in the drag selection
        bool[] m_SelectedFrameBuffer;
        bool m_HasFrameSelection;
        int m_SelectedFrameBaseFrame;

        /// <summary>
        /// After a drag-completed event, contains a boolean buffer where
        /// buffer[frameIndex - SelectedFrameBaseFrame] == true for selected frames.
        /// Check HasFrameSelection before accessing.
        /// </summary>
        public bool[] SelectedFrameBuffer => m_HasFrameSelection ? m_SelectedFrameBuffer : null;
        public bool HasFrameSelection => m_HasFrameSelection;
        public int SelectedFrameBaseFrame => m_SelectedFrameBaseFrame;

        // Cached tooltip bar index to avoid per-move string allocations
        int m_LastTooltipBar = -1;

        // Last known mouse X within the graph, for WASD zoom anchor
        float m_LastMouseNormX = 0.5f;

        // Per-frame scratch buffer
        long[] m_PerFrameBuffer;

        // Per-bar analyzed mask (reusable, sized to bar count)
        bool[] m_AnalyzedBarMask;

        // Overview strip
        const float k_OverviewHeight = 24f;
        GraphElement m_OverviewElement;
        VisualElement m_ViewportRect;
        BarData[] m_OverviewBars;
        int m_OverviewBarCount;
        bool m_OverviewDragging;
        float m_OverviewDragStartX;
        float m_OverviewDragStartVP;

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
        //  VIEWPORT — zoom / pan
        // ═══════════════════════════════════════════════════

        /// <summary>Current viewport normalized start [0,1].</summary>
        public float ViewportStart => m_ViewportStart;

        /// <summary>Current viewport normalized end [0,1].</summary>
        public float ViewportEnd => m_ViewportEnd;

        /// <summary>True when the detail view is zoomed in.</summary>
        public bool IsZoomedIn => m_ViewportEnd - m_ViewportStart < 0.999f;

        public void ResetViewport()
        {
            m_ViewportStart = 0f;
            m_ViewportEnd = 1f;
            RebuildGraph();
        }

        void ZoomViewport(float zoomFactor, float anchorNormalized)
        {
            float span = m_ViewportEnd - m_ViewportStart;
            float newSpan = Mathf.Clamp(span * zoomFactor, k_MinVisibleBars / Mathf.Max(1, m_TotalBucketCount), 1f);
            float anchor = m_ViewportStart + span * anchorNormalized;

            m_ViewportStart = anchor - newSpan * anchorNormalized;
            m_ViewportEnd = m_ViewportStart + newSpan;
            ClampViewport();
            RebuildGraph();
        }

        void PanViewport(float delta)
        {
            float span = m_ViewportEnd - m_ViewportStart;
            m_ViewportStart += delta;
            m_ViewportEnd = m_ViewportStart + span;
            ClampViewport();
            RebuildGraph();
        }

        void ClampViewport()
        {
            float span = m_ViewportEnd - m_ViewportStart;
            if (m_ViewportStart < 0f)
            {
                m_ViewportStart = 0f;
                m_ViewportEnd = span;
            }
            if (m_ViewportEnd > 1f)
            {
                m_ViewportEnd = 1f;
                m_ViewportStart = 1f - span;
            }
            if (m_ViewportStart < 0f) m_ViewportStart = 0f;
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
        Scroller m_HScroller;

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

        public GraphControllerState CaptureState()
        {
            return new GraphControllerState
            {
                OrderByMagnitude = m_OrderByMagnitude,
                ViewportStart = m_ViewportStart,
                ViewportEnd = m_ViewportEnd,
                HasFrameSelection = m_HasFrameSelection,
                SelectedFrameBuffer = m_HasFrameSelection ? m_SelectedFrameBuffer : null,
                SelectedFrameBaseFrame = m_SelectedFrameBaseFrame,
                SelectionFrameStart = m_SelectionFrameStart,
                SelectionFrameEnd = m_SelectionFrameEnd,
                HighlightedFrame = m_HighlightedFrame
            };
        }

        public void RestoreState(GraphControllerState state)
        {
            m_OrderByMagnitude = state.OrderByMagnitude;
            m_ViewportStart = state.ViewportStart;
            m_ViewportEnd = state.ViewportEnd;
            m_HasFrameSelection = state.HasFrameSelection;
            m_SelectedFrameBuffer = state.SelectedFrameBuffer;
            m_SelectedFrameBaseFrame = state.SelectedFrameBaseFrame;
            m_SelectionFrameStart = state.SelectionFrameStart;
            m_SelectionFrameEnd = state.SelectionFrameEnd;
            m_HighlightedFrame = state.HighlightedFrame;
            UpdateSortToggleLabel();
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
            m_TotalBucketCount = bucketCount;

            // ── Ensure bar buffer capacity ──
            EnsureBarCapacity(ref m_Bars, bucketCount);

            // ── Fill buckets and find max (over ALL buckets for consistent Y axis) ──
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

            // ── Compute X/W/Height for each bar (pre-sort, pre-viewport) ──
            float fullBarWidth = areaWidth / bucketCount;
            for (int b = 0; b < bucketCount; b++)
            {
                m_Bars[b].X = b * fullBarWidth;
                m_Bars[b].W = Mathf.Max(fullBarWidth, 1f);
                m_Bars[b].Height = maxValue > 0
                    ? (float)m_Bars[b].Value / maxValue * k_GraphHeight
                    : 0f;
            }

            // ── Sorted view (order by magnitude) ──
            if (m_OrderByMagnitude)
                ApplySortedOrder(bucketCount, fullBarWidth);
            // (when !m_OrderByMagnitude the sorted indices are simply not used)

            // ── Viewport clipping — only layout visible bars ──
            int visibleStart = Mathf.FloorToInt(m_ViewportStart * bucketCount);
            int visibleEnd = Mathf.CeilToInt(m_ViewportEnd * bucketCount);
            visibleStart = Mathf.Clamp(visibleStart, 0, bucketCount - 1);
            visibleEnd = Mathf.Clamp(visibleEnd, visibleStart, bucketCount);
            int visibleCount = visibleEnd - visibleStart;
            m_ViewportStartBucket = visibleStart;

            if (visibleCount <= 0)
            {
                m_BarCount = 0;
                m_GraphElement.SetBarData(m_Bars, 0);
                return;
            }

            // Recompute X/W for visible bars to fill the graph width.
            // Forward copy is safe: i <= srcIdx always, so no data is overwritten before it is read.
            float visibleBarWidth = areaWidth / visibleCount;
            for (int i = 0; i < visibleCount; i++)
            {
                int srcIdx = visibleStart + i;
                m_Bars[i] = m_Bars[srcIdx];
                m_Bars[i].X = i * visibleBarWidth;
                m_Bars[i].W = Mathf.Max(visibleBarWidth, 1f);
                // Recalculate height against global max (unchanged)
                m_Bars[i].Height = maxValue > 0
                    ? (float)m_Bars[i].Value / maxValue * k_GraphHeight
                    : 0f;
            }
            m_BarCount = visibleCount;

            // ── Grid lines ──
            ComputeGridLines(maxValue, k_GraphHeight);

            // ── Build per-bar analyzed mask for dimming ──
            BuildAnalyzedBarMask();

            // ── Push data to GraphElement ──
            m_GraphElement.YAxisMax = maxValue;
            m_GraphElement.SetBarData(m_Bars, m_BarCount);
            m_GraphElement.SetGridLines(m_GridLines, m_GridLineCount);
            m_GraphElement.SetAnalyzedMask(m_AnalyzedBarMask);
            m_GraphElement.HasData = true;

            // ── Update axis labels ──
            m_GraphYMax.text = GCAllocUtils.FormatBytes(maxValue);
            m_GraphYMid.text = GCAllocUtils.FormatBytes(maxValue / 2);
            if (m_BarCount > 0)
            {
                m_GraphXStart.text = GCAllocUtils.DisplayFrame(m_Bars[0].StartFrame).ToString();
                m_GraphXEnd.text = GCAllocUtils.DisplayFrame(m_Bars[m_BarCount - 1].EndFrame).ToString();
            }

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

            // ── Update horizontal scroller ──
            UpdateHorizontalScroller();

            // ── Overview strip bars (always full range, no viewport clipping) ──
            RebuildOverviewStrip(perFrame, frameCount, maxValue);
            UpdateViewportRect();

            // ── Restore selection from frame coordinates ──
            RestoreSelectionFromFrames();

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
                int origBucket = m_OrderByMagnitude
                    ? m_SortedBarIndices[b + m_ViewportStartBucket]
                    : b + m_ViewportStartBucket;
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
        /// The frame buffer is preserved so dimming stays correct across mode switches.
        /// </summary>
        public void ClearSelection()
        {
            m_GraphElement.SetSelection(-1, -1);
            m_GraphElement.SetHighlightedBar(-1);
            m_LastSelectionStartBar = -1;
            m_LastSelectionEndBar = -1;
            m_SelectionFrameStart = -1;
            m_SelectionFrameEnd = -1;
            m_HighlightedFrame = -1;
        }

        /// <summary>
        /// Clear the frame selection buffer used for per-bar dimming.
        /// Call when resetting to full range or starting a fresh analysis.
        /// </summary>
        public void ClearFrameSelection()
        {
            m_HasFrameSelection = false;
        }

        /// <summary>
        /// Remap frame-based selection/highlight to current visible bar indices.
        /// Called after every RebuildGraph to keep visual selection in sync.
        /// </summary>
        void RestoreSelectionFromFrames()
        {
            if (m_BarCount == 0)
            {
                m_GraphElement.SetSelection(-1, -1);
                m_GraphElement.SetHighlightedBar(-1);
                m_LastSelectionStartBar = -1;
                m_LastSelectionEndBar = -1;
                return;
            }

            // Restore range selection
            if (m_SelectionFrameStart >= 0 && m_SelectionFrameEnd >= 0)
            {
                int sBar = FindBarContainingFrame(m_SelectionFrameStart);
                int eBar = FindBarContainingFrame(m_SelectionFrameEnd);

                // If selection is partially off-screen, clamp to visible bars
                if (sBar < 0 && eBar >= 0) sBar = 0;
                if (eBar < 0 && sBar >= 0) eBar = m_BarCount - 1;

                if (sBar >= 0 && eBar >= 0)
                {
                    m_GraphElement.SetSelection(sBar, eBar);
                    m_LastSelectionStartBar = sBar;
                    m_LastSelectionEndBar = eBar;
                }
                else
                {
                    m_GraphElement.SetSelection(-1, -1);
                    m_LastSelectionStartBar = -1;
                    m_LastSelectionEndBar = -1;
                }
            }
            else
            {
                m_GraphElement.SetSelection(-1, -1);
                m_LastSelectionStartBar = -1;
                m_LastSelectionEndBar = -1;
            }

            // Restore single-bar highlight
            if (m_HighlightedFrame >= 0)
            {
                int hBar = FindBarContainingFrame(m_HighlightedFrame);
                m_GraphElement.SetHighlightedBar(hBar);
            }
            else
            {
                m_GraphElement.SetHighlightedBar(-1);
            }
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

            // Make focusable for keyboard input; auto-focus on hover
            m_GraphElement.focusable = true;
            m_GraphElement.RegisterCallback<PointerEnterEvent, GraphElement>(
                OnGraphPointerEnter, m_GraphElement);
            m_GraphElement.RegisterCallback<KeyDownEvent>(OnGraphKeyDown);
            m_GraphElement.RegisterCallback<WheelEvent>(OnGraphWheel);

            // Subscribe to GraphElement events
            m_GraphElement.BarClicked += OnBarClicked;
            m_GraphElement.SelectionChanged += OnSelectionChangedInternal;
            m_GraphElement.DragCompleted += OnDragCompletedInternal;

            // ── Overview strip (miniature full-range view) ──
            var overviewContainer = new VisualElement
            {
                style =
                {
                    height = k_OverviewHeight,
                    marginBottom = 2,
                    flexShrink = 0
                }
            };

            m_OverviewElement = new GraphElement
            {
                style =
                {
                    height = k_OverviewHeight,
                    flexGrow = 1
                }
            };
            m_OverviewElement.pickingMode = PickingMode.Ignore;

            m_ViewportRect = new VisualElement
            {
                style =
                {
                    position = Position.Absolute,
                    top = 0,
                    bottom = 0,
                    backgroundColor = new Color(1f, 1f, 1f, 0.15f),
                    borderLeftWidth = 1, borderRightWidth = 1,
                    borderLeftColor = new Color(1f, 1f, 1f, 0.5f),
                    borderRightColor = new Color(1f, 1f, 1f, 0.5f)
                }
            };

            overviewContainer.Add(m_OverviewElement);
            overviewContainer.Add(m_ViewportRect);
            overviewContainer.RegisterCallback<PointerDownEvent>(OnOverviewPointerDown);
            overviewContainer.RegisterCallback<PointerMoveEvent>(OnOverviewPointerMove);
            overviewContainer.RegisterCallback<PointerUpEvent>(OnOverviewPointerUp);

            chartColumn.Add(overviewContainer);

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

            // ── Horizontal scroller (visible when zoomed) ──
            m_HScroller = new Scroller(0, 1, OnScrollerChanged, SliderDirection.Horizontal)
            {
                style =
                {
                    height = 14,
                    display = DisplayStyle.None
                }
            };
            chartColumn.Add(m_HScroller);

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

        void OnScrollerChanged(float value)
        {
            float span = m_ViewportEnd - m_ViewportStart;
            m_ViewportStart = value;
            m_ViewportEnd = value + span;
            ClampViewport();
            RebuildGraph();
        }

        void OnGraphGeometryChanged(GeometryChangedEvent evt)
        {
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData) return;
            float oldW = evt.oldRect.width;
            float newW = evt.newRect.width;
            if (Mathf.Abs(newW - oldW) < 2f) return;
            RebuildGraph();
        }

        void OnGraphKeyDown(KeyDownEvent evt)
        {
            if (m_TotalBucketCount == 0) return;
            bool handled = false;
            float span = m_ViewportEnd - m_ViewportStart;
            float panStep = span * 0.15f;

            switch (evt.keyCode)
            {
                case KeyCode.W:
                    ZoomViewport(0.7f, m_LastMouseNormX);
                    handled = true;
                    break;
                case KeyCode.S:
                    ZoomViewport(1.4f, m_LastMouseNormX);
                    handled = true;
                    break;
                case KeyCode.A:
                    PanViewport(-panStep);
                    handled = true;
                    break;
                case KeyCode.D:
                    PanViewport(panStep);
                    handled = true;
                    break;
            }

            if (handled)
                evt.StopPropagation();
        }

        void OnGraphWheel(WheelEvent evt)
        {
            if (m_TotalBucketCount == 0) return;

            // Compute anchor as normalized position within the graph
            float localX = evt.localMousePosition.x;
            float areaWidth = m_GraphElement.contentRect.width;
            float anchor = areaWidth > 0 ? Mathf.Clamp01(localX / areaWidth) : 0.5f;

            float zoomFactor = evt.delta.y > 0 ? 1.2f : 0.8f;
            ZoomViewport(zoomFactor, anchor);
            evt.StopPropagation();
        }

        void OnBarClicked(int barIndex)
        {
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData) return;
            var perFrame = m_FrameStore.FullFrameBytes;
            if (barIndex < 0 || barIndex >= m_BarCount) return;

            // Resolve the actual bar index, accounting for viewport offset and sorting
            int resolvedIndex = m_OrderByMagnitude
                ? m_SortedBarIndices[barIndex + m_ViewportStartBucket]
                : barIndex + m_ViewportStartBucket;

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
            m_HighlightedFrame = frameIndex;

            if (m_OnFrameSelected != null)
                m_OnFrameSelected(frameIndex);
        }

        void OnSelectionChangedInternal(int startBar, int endBar)
        {
            m_LastSelectionStartBar = startBar;
            m_LastSelectionEndBar = endBar;
            MapBarRangeToFrameRange(startBar, endBar, out int startFrame, out int endFrame);
            m_SelectionFrameStart = startFrame;
            m_SelectionFrameEnd = endFrame;
            if (OnSelectionChanged != null)
                OnSelectionChanged(startFrame, endFrame);
        }

        void OnDragCompletedInternal(int startBar, int endBar)
        {
            m_LastSelectionStartBar = startBar;
            m_LastSelectionEndBar = endBar;
            MapBarRangeToFrameRange(startBar, endBar, out int startFrame, out int endFrame);
            m_SelectionFrameStart = startFrame;
            m_SelectionFrameEnd = endFrame;
            BuildSelectedFrameBuffer(startBar, endBar);
            if (OnDragCompleted != null)
                OnDragCompleted(startFrame, endFrame);
        }

        static void OnGraphPointerEnter(PointerEnterEvent evt, GraphElement graph) => graph.Focus();

        void OnGraphPointerMove(PointerMoveEvent evt)
        {
            // Track mouse position for WASD zoom anchor
            float areaW = m_GraphElement.contentRect.width;
            if (areaW > 0f)
                m_LastMouseNormX = Mathf.Clamp01(evt.localPosition.x / areaW);

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
            bool hasActiveSelection = m_LastSelectionStartBar >= 0 && m_LastSelectionEndBar >= 0;

            evt.menu.AppendAction("Select All", OnContextSelectAll,
                hasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendAction("Clear Selection", OnContextClearSelection,
                hasActiveSelection ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendSeparator();

            evt.menu.AppendAction("Select Frame with Max GC", OnContextSelectMaxGC,
                hasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendAction("Analyze Selection", OnContextAnalyzeSelection,
                hasActiveSelection ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendSeparator();

            evt.menu.AppendAction("Reset Zoom", OnContextResetZoom,
                IsZoomedIn ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendAction(
                m_OrderByMagnitude ? "Order by Frame" : "Order by Size",
                OnContextToggleSort,
                hasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        void OnContextSelectAll(DropdownMenuAction _)
        {
            m_GraphElement.SetSelection(0, m_BarCount - 1);
            m_LastSelectionStartBar = 0;
            m_LastSelectionEndBar = m_BarCount - 1;
            if (m_BarCount > 0)
            {
                m_SelectionFrameStart = m_Bars[0].StartFrame;
                m_SelectionFrameEnd = m_Bars[m_BarCount - 1].EndFrame;
            }
            NotifySelectionChanged(0, m_BarCount - 1);
        }

        void OnContextClearSelection(DropdownMenuAction _)
        {
            ClearSelection();
        }

        void OnContextSelectMaxGC(DropdownMenuAction _) => SelectExtremeFrame(true);

        void OnContextAnalyzeSelection(DropdownMenuAction _)
        {
            if (m_LastSelectionStartBar < 0 || m_LastSelectionEndBar < 0) return;
            MapBarRangeToFrameRange(m_LastSelectionStartBar, m_LastSelectionEndBar,
                out int rangeStart, out int rangeEnd);
            BuildSelectedFrameBuffer(m_LastSelectionStartBar, m_LastSelectionEndBar);
            if (OnDragCompleted != null)
                OnDragCompleted(rangeStart, rangeEnd);
        }

        void OnContextResetZoom(DropdownMenuAction _) => ResetViewport();

        void OnContextToggleSort(DropdownMenuAction _) => OnSortToggleClicked();

        void SelectExtremeFrame(bool selectMax)
        {
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData) return;
            if (m_FramesPerBucket <= 0) return;

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

            // Global bucket index for the extreme frame
            int globalBucketIdx = extremeIdx / m_FramesPerBucket;
            if (globalBucketIdx >= m_TotalBucketCount)
                globalBucketIdx = m_TotalBucketCount - 1;

            // If the target bucket is outside the current viewport, pan to include it
            if (!m_OrderByMagnitude && IsZoomedIn)
            {
                float bucketNorm = (float)globalBucketIdx / m_TotalBucketCount;
                float vpSpan = m_ViewportEnd - m_ViewportStart;
                if (bucketNorm < m_ViewportStart || bucketNorm >= m_ViewportEnd)
                {
                    // Center the viewport on the target bucket
                    m_ViewportStart = bucketNorm - vpSpan * 0.5f;
                    m_ViewportEnd = m_ViewportStart + vpSpan;
                    ClampViewport();
                    RebuildGraph();
                }
            }

            // Convert global bucket to display-local bar index
            int barIdx = globalBucketIdx - m_ViewportStartBucket;
            if (barIdx < 0) barIdx = 0;
            if (barIdx >= m_BarCount) barIdx = m_BarCount - 1;

            // If sorted, find the display position of the original bucket
            if (m_OrderByMagnitude)
            {
                for (int i = 0; i < m_BarCount; i++)
                {
                    if (m_SortedBarIndices[i] == globalBucketIdx)
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
            m_SelectionFrameStart = m_Bars[barIdx].StartFrame;
            m_SelectionFrameEnd = m_Bars[barIdx].EndFrame;
            m_HighlightedFrame = frameIndex;

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
                    Value = v,
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

            // Use actual content rect height for positioning (may differ from k_GraphHeight)
            float actualHeight = m_GraphElement.contentRect.height;
            if (float.IsNaN(actualHeight) || actualHeight < 1f) actualHeight = k_GraphHeight;

            // Position labels for current grid lines
            for (int i = 0; i < m_GridLineCount && i < k_MaxGridLineLabels; i++)
            {
                Label label = EnsureGridLineLabel(i);
                label.text = m_GridLines[i].Label;
                label.style.display = DisplayStyle.Flex;
                // Position from bottom, scaling the grid line value to actual height
                label.style.position = Position.Absolute;
                float bottomPos = m_YAxisMax > 0
                    ? (float)m_GridLines[i].Value / m_YAxisMax * actualHeight
                    : m_GridLines[i].Y;
                label.style.bottom = bottomPos - 6; // center the 12px label on the line
                label.style.right = 4;
            }

            m_GridLineLabelCount = Mathf.Min(m_GridLineCount, k_MaxGridLineLabels);
        }

        // ═══════════════════════════════════════════════════
        //  SORTING
        // ═══════════════════════════════════════════════════

        // Non-allocating struct comparer for Array.Sort — sorts bar indices by value descending
        struct BarValueDescComparer : IComparer<int>
        {
            public BarData[] Bars;
            public int Compare(int a, int b) => Bars[b].Value.CompareTo(Bars[a].Value);
        }

        void ApplySortedOrder(int count, float barWidth)
        {
            // Build index array: maps display position -> original bucket index
            if (m_SortedBarIndices == null || m_SortedBarIndices.Length < count)
                m_SortedBarIndices = new int[count];

            for (int i = 0; i < count; i++)
                m_SortedBarIndices[i] = i;

            // Sort descending by value — O(n log n) via Array.Sort with struct comparer (no allocations)
            var comparer = new BarValueDescComparer { Bars = m_Bars };
            Array.Sort(m_SortedBarIndices, 0, count, comparer);

            // Rearrange bars into display order so array index == display position.
            // This ensures GraphElement's selection (which uses display indices) lines up.
            EnsureBarCapacity(ref m_SortScratch, count);
            for (int displayIdx = 0; displayIdx < count; displayIdx++)
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

        void UpdateHorizontalScroller()
        {
            if (m_HScroller == null) return;

            if (!IsZoomedIn)
            {
                m_HScroller.style.display = DisplayStyle.None;
                return;
            }

            m_HScroller.style.display = DisplayStyle.Flex;
            float span = m_ViewportEnd - m_ViewportStart;
            m_HScroller.lowValue = 0;
            m_HScroller.highValue = Mathf.Max(0, 1f - span);
            m_HScroller.slider.pageSize = span;
            m_HScroller.Adjust(span);
            m_HScroller.slider.SetValueWithoutNotify(m_ViewportStart);
        }

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

        void RebuildOverviewStrip(long[] perFrame, int frameCount, long maxValue)
        {
            if (m_OverviewElement == null) return;

            float overviewWidth = m_OverviewElement.contentRect.width;
            if (float.IsNaN(overviewWidth) || overviewWidth < 1f) overviewWidth = 400f;

            int overviewBucketSize = 1;
            if (frameCount > (int)overviewWidth)
                overviewBucketSize = Mathf.CeilToInt((float)frameCount / Mathf.Max(1f, overviewWidth));

            int bucketCount = Mathf.CeilToInt((float)frameCount / overviewBucketSize);
            EnsureBarCapacity(ref m_OverviewBars, bucketCount);
            m_OverviewBarCount = bucketCount;

            float barWidth = overviewWidth / bucketCount;
            for (int b = 0; b < bucketCount; b++)
            {
                int startIdx = b * overviewBucketSize;
                int endIdx = Mathf.Min(startIdx + overviewBucketSize, frameCount);
                long bucketMax = 0;
                for (int i = startIdx; i < endIdx; i++)
                    if (perFrame[i] > bucketMax) bucketMax = perFrame[i];

                m_OverviewBars[b] = new BarData
                {
                    X = b * barWidth,
                    W = Mathf.Max(barWidth, 1f),
                    Height = maxValue > 0 ? (float)bucketMax / maxValue * k_OverviewHeight : 0f,
                    Value = bucketMax,
                    StartFrame = m_FrameStore.FullFrameStart + startIdx,
                    EndFrame = m_FrameStore.FullFrameStart + endIdx - 1
                };
            }

            m_OverviewElement.YAxisMax = maxValue;
            m_OverviewElement.SetBarData(m_OverviewBars, m_OverviewBarCount);
            // Overview intentionally does not show overlay bars — too small to be useful.

            // Apply analyzed-range dimming to overview too
            if (m_AnalyzedFrameStart >= 0 && m_AnalyzedFrameEnd >= 0)
            {
                int aStart = -1, aEnd = -1;
                for (int i = 0; i < m_OverviewBarCount; i++)
                {
                    if (m_OverviewBars[i].EndFrame >= m_AnalyzedFrameStart &&
                        m_OverviewBars[i].StartFrame <= m_AnalyzedFrameEnd)
                    {
                        if (aStart < 0) aStart = i;
                        aEnd = i;
                    }
                }
                m_OverviewElement.SetAnalyzedRange(aStart, aEnd);
            }
            else
            {
                m_OverviewElement.SetAnalyzedRange(-1, -1);
            }

            m_OverviewElement.HasData = true;
        }

        void UpdateViewportRect()
        {
            if (m_ViewportRect == null || m_OverviewElement == null) return;
            float overviewWidth = m_OverviewElement.contentRect.width;
            if (float.IsNaN(overviewWidth) || overviewWidth < 1f) return;

            m_ViewportRect.style.left = m_ViewportStart * overviewWidth;
            m_ViewportRect.style.width = (m_ViewportEnd - m_ViewportStart) * overviewWidth;

            // Hide overview strip when fully zoomed out
            bool show = IsZoomedIn;
            m_OverviewElement.parent.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void OnOverviewPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || m_OverviewBarCount == 0) return;

            float overviewWidth = m_OverviewElement.contentRect.width;
            if (overviewWidth < 1f) return;

            float clickNorm = evt.localPosition.x / overviewWidth;
            float span = m_ViewportEnd - m_ViewportStart;

            // Check if clicking within viewport rect -> start drag
            if (clickNorm >= m_ViewportStart && clickNorm <= m_ViewportEnd)
            {
                m_OverviewDragging = true;
                m_OverviewDragStartX = evt.localPosition.x;
                m_OverviewDragStartVP = m_ViewportStart;
                ((VisualElement)evt.target).CapturePointer(evt.pointerId);
            }
            else
            {
                // Click outside -> jump viewport center to click position
                float newStart = clickNorm - span * 0.5f;
                m_ViewportStart = newStart;
                m_ViewportEnd = newStart + span;
                ClampViewport();
                RebuildGraph();
            }

            evt.StopPropagation();
        }

        void OnOverviewPointerMove(PointerMoveEvent evt)
        {
            if (!m_OverviewDragging) return;

            float overviewWidth = m_OverviewElement.contentRect.width;
            if (overviewWidth < 1f) return;

            float dx = evt.localPosition.x - m_OverviewDragStartX;
            float deltaNorm = dx / overviewWidth;
            float span = m_ViewportEnd - m_ViewportStart;

            m_ViewportStart = m_OverviewDragStartVP + deltaNorm;
            m_ViewportEnd = m_ViewportStart + span;
            ClampViewport();
            RebuildGraph();

            evt.StopPropagation();
        }

        void OnOverviewPointerUp(PointerUpEvent evt)
        {
            if (evt.button != 0) return;
            if (m_OverviewDragging)
            {
                m_OverviewDragging = false;
                ((VisualElement)evt.target).ReleasePointer(evt.pointerId);
            }
            evt.StopPropagation();
        }

        /// <summary>
        /// Build a per-bar boolean mask indicating which bars are in the analyzed
        /// range. Works for both contiguous (frame-order) and non-contiguous
        /// (sorted) views. mask[i] == true means bar i is at full brightness.
        /// </summary>
        void BuildAnalyzedBarMask()
        {
            // Ensure capacity
            if (m_AnalyzedBarMask == null || m_AnalyzedBarMask.Length < m_BarCount)
                m_AnalyzedBarMask = new bool[Mathf.Max(m_BarCount, 64)];
            else
                Array.Clear(m_AnalyzedBarMask, 0, m_BarCount);

            if (m_FrameStore == null || m_BarCount == 0) return;

            // No analysis active, or full range is analyzed — all bars at full brightness
            if (m_AnalyzedFrameStart < 0 || m_AnalyzedFrameEnd < 0
                || (m_AnalyzedFrameStart == m_FrameStore.FullFrameStart
                    && m_AnalyzedFrameEnd == m_FrameStore.FullFrameEnd))
            {
                for (int i = 0; i < m_BarCount; i++)
                    m_AnalyzedBarMask[i] = true;
                return;
            }

            // With a frame buffer, check each bar's frames against the buffer
            // for precise per-bar dimming. Works in both sorted and frame-order modes.
            if (m_HasFrameSelection)
            {
                int baseFrame = m_SelectedFrameBaseFrame;
                int bufLen = m_SelectedFrameBuffer.Length;
                for (int i = 0; i < m_BarCount; i++)
                {
                    int sf = m_Bars[i].StartFrame;
                    int ef = m_Bars[i].EndFrame;
                    bool any = false;
                    for (int f = sf; f <= ef; f++)
                    {
                        int idx = f - baseFrame;
                        if (idx >= 0 && idx < bufLen && m_SelectedFrameBuffer[idx])
                        {
                            any = true;
                            break;
                        }
                    }
                    m_AnalyzedBarMask[i] = any;
                }
                return;
            }

            // Frame-order mode — bar overlaps analyzed frame range
            for (int i = 0; i < m_BarCount; i++)
            {
                m_AnalyzedBarMask[i] = m_Bars[i].StartFrame <= m_AnalyzedFrameEnd
                    && m_Bars[i].EndFrame >= m_AnalyzedFrameStart;
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

        int FindBarContainingFrame(int frameIndex)
        {
            for (int i = 0; i < m_BarCount; i++)
            {
                if (m_Bars[i].StartFrame <= frameIndex && m_Bars[i].EndFrame >= frameIndex)
                    return i;
            }
            return -1;
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
            if (m_OrderByMagnitude)
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

        /// <summary>
        /// Populate the reusable bool[] buffer with the frames covered by the
        /// selected display bars. Works for both contiguous and sorted views.
        /// </summary>
        void BuildSelectedFrameBuffer(int startBar, int endBar)
        {
            if (startBar > endBar)
            {
                int tmp = startBar;
                startBar = endBar;
                endBar = tmp;
            }
            if (startBar < 0) startBar = 0;
            if (endBar >= m_BarCount) endBar = m_BarCount - 1;

            int fullFrameCount = m_FrameStore.FullFrameBytes.Length;
            m_SelectedFrameBaseFrame = m_FrameStore.FullFrameStart;

            // Ensure capacity, then clear
            if (m_SelectedFrameBuffer == null || m_SelectedFrameBuffer.Length < fullFrameCount)
                m_SelectedFrameBuffer = new bool[fullFrameCount];
            else
                System.Array.Clear(m_SelectedFrameBuffer, 0, fullFrameCount);

            // Mark frames covered by each selected bar
            for (int i = startBar; i <= endBar; i++)
            {
                int sf = m_Bars[i].StartFrame;
                int ef = m_Bars[i].EndFrame;
                for (int f = sf; f <= ef; f++)
                {
                    int idx = f - m_SelectedFrameBaseFrame;
                    if (idx >= 0 && idx < fullFrameCount)
                        m_SelectedFrameBuffer[idx] = true;
                }
            }

            m_HasFrameSelection = true;
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
