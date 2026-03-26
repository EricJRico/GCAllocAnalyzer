using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using GCAllocBreakdown.BarChart.Core;
using GCAllocBreakdown.BarChart.Events;
using GCAllocBreakdown.BarChart.Input;
using GCAllocBreakdown.BarChart.Input.Handlers;

namespace GCAllocBreakdown.Editor
{
    // ═══════════════════════════════════════════════════
    //  PER-FRAME GRAPH CONTROLLER
    //  Manages the per-frame bar graph using BarGraphElement
    //  from ui-toolkit-extensions. The main window creates
    //  an instance and delegates all graph operations to it.
    // ═══════════════════════════════════════════════════

    internal class PerFrameGraphController
    {
        // ═══════════════════════════════════════════════════
        //  CONSTANTS
        // ═══════════════════════════════════════════════════

        const float k_OverviewHeight = 24f;
        const float k_GraphXAxisHeight = 16f;
        const float k_MinGraphHeight = 100f;
        // ═══════════════════════════════════════════════════
        //  COLORS
        // ═══════════════════════════════════════════════════

        static readonly Color k_DimGray = new(0.7f, 0.7f, 0.7f);
        static readonly Color k_TooltipBg = new(0.12f, 0.12f, 0.12f);
        static readonly Color k_TooltipBorder = new(0.4f, 0.4f, 0.4f);

        // ═══════════════════════════════════════════════════
        //  CALLBACKS
        // ═══════════════════════════════════════════════════

        readonly Action<int, string> m_OnFrameSelected;
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
        bool m_GroupByCallsite;

        // ═══════════════════════════════════════════════════
        //  BAR DATA BUFFERS
        // ═══════════════════════════════════════════════════

        BarEntry[] m_BarEntries;
        BarSegment[] m_BarSegments;
        int m_BarEntryCount;
        int m_BarSegmentCount;

        // Per-bar selection buffer for baked dimming (reusable, never freed)
        bool[] m_SelectionBuffer;
        bool m_HasSelection;

        // Overlay values (one float per bar for method highlight)
        float[] m_OverlayValues;

        // Segment data built from cached analysis
        // m_SegmentFlatArray[frameRelativeIndex * methodCount + methodIndex] = bytes
        long[] m_SegmentFlatArray;
        int[] m_SegmentSortIndices;
        long[] m_SegmentSortValues;
        bool m_HasSegmentData;
        MethodColorPalette m_MethodPalette = new();

        // Timing from last SetData — reported in RebuildGraph log
        long m_LastMsPalette;
        long m_LastMsSegments;
        readonly StringBuilder m_LogSB = new(256);

        // Per-frame scratch buffer for overlay building
        long[] m_PerFrameBuffer;

        // Last bar/dim colors baked into m_BarEntries — used to skip BuildBarEntries() in ApplySettings()
        Color32 m_LastBakedBarColor;
        Color32 m_LastBakedDimColor;


        // ═══════════════════════════════════════════════════
        //  UI ELEMENTS
        // ═══════════════════════════════════════════════════

        VisualElement m_GraphSection;
        BarGraphElement m_BarGraph;
        BarGraphOverviewStrip m_OverviewStrip;
        Label m_GraphXStart, m_GraphXEnd;
        Label m_GraphOverlayLabel;
        Button m_SortToggleBtn;
        Button m_ResetBtn;
        Button m_YAxisResetBtn;
        Label m_FloatingTooltip;

        // Cached tooltip state
        int m_LastTooltipBar = -1;
        Vector2 m_LastPointerLocalPos;

        // Frame tracking
        int m_HighlightedFrame = -1;

        // ═══════════════════════════════════════════════════
        //  PUBLIC PROPERTIES
        // ═══════════════════════════════════════════════════

        /// <summary>The root element — add this to the parent layout.</summary>
        public VisualElement Root => m_GraphSection;

        /// <summary>
        /// The floating tooltip. Must be added to the window's rootVisualElement
        /// so it renders above all other content.
        /// </summary>
        public VisualElement TooltipElement => m_FloatingTooltip;

        /// <summary>True when the view is zoomed in (ZoomX > 1).</summary>
        public bool IsZoomedIn => m_BarGraph != null && m_BarGraph.ViewState.ZoomX > 1.01f;

        /// <summary>Enable or disable the Order by Size button.</summary>
        public void SetSortEnabled(bool enabled)
        {
            if (!enabled)
                OrderByMagnitude = false;
            m_SortToggleBtn.SetEnabled(enabled);
        }

        /// <summary>When true, bars are sorted by magnitude (descending).</summary>
        public bool OrderByMagnitude
        {
            get => m_BarGraph != null && m_BarGraph.ViewState.SortMode == SortMode.ByValue;
            set
            {
                if (m_BarGraph == null) return;
                var mode = value ? SortMode.ByValue : SortMode.None;
                if (m_BarGraph.ViewState.SortMode == mode) return;
                m_BarGraph.SetSortMode(mode, descending: true);

                UpdateSortToggleLabel();
            }
        }

        // ═══════════════════════════════════════════════════
        //  CONSTRUCTOR
        // ═══════════════════════════════════════════════════

        public PerFrameGraphController(Action<int, string> onFrameSelected, Func<int> getSelectedMarkerIndex)
        {
            m_OnFrameSelected = onFrameSelected;
            m_GetSelectedMarkerIndex = getSelectedMarkerIndex;
            BuildUI();
        }

        // ═══════════════════════════════════════════════════
        //  BUILD UI
        // ═══════════════════════════════════════════════════

        void BuildUI()
        {
            m_GraphSection = new VisualElement
            {
                style =
                {
                    flexGrow = 1,
                    marginLeft = 4,
                    marginRight = 4,
                    minHeight = k_MinGraphHeight + k_GraphXAxisHeight + k_OverviewHeight + 40,
                    display = DisplayStyle.None
                }
            };

            // ── Header row ──
            var headerRow = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    backgroundColor = new Color(0.25f, 0.25f, 0.25f),
                    paddingTop = 4,
                    paddingBottom = 4,
                    paddingLeft = 4,
                    paddingRight = 4,
                    marginBottom = 2,
                    borderBottomWidth = 1,
                    borderBottomColor = new Color(0.15f, 0.15f, 0.15f)
                }
            };
            headerRow.Add(new Label("Per-Frame Graph")
            {
                style =
                {
                    fontSize = 12,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    flexGrow = 1
                }
            });

            m_YAxisResetBtn = new Button(ResetYAxisScale)
            {
                text = "Reset Y",
                tooltip = "Reset Y-axis to auto-fit",
                style =
                {
                    fontSize = 10,
                    paddingLeft = 4, paddingRight = 4,
                    paddingTop = 1, paddingBottom = 1,
                    marginRight = 4
                }
            };
            m_YAxisResetBtn.SetEnabled(false);
            headerRow.Add(m_YAxisResetBtn);

            m_SortToggleBtn = new Button(OnSortToggleClicked)
            {
                text = "Order by Size",
                tooltip = "Toggle between frame order and order by allocation size (descending)",
                style =
                {
                    fontSize = 10,
                    paddingLeft = 4, paddingRight = 4,
                    paddingTop = 1, paddingBottom = 1,
                    marginRight = 4
                }
            };
            headerRow.Add(m_SortToggleBtn);

            m_ResetBtn = new Button(() =>
            {
                ResetYAxisScale();
                OnResetRequested?.Invoke();
            })
            {
                text = "Reset to Full Range",
                tooltip = "Restore full-range analysis from cache (instant)",
                style =
                {
                    fontSize = 10,
                    paddingLeft = 4, paddingRight = 4,
                    paddingTop = 1, paddingBottom = 1
                }
            };
            m_ResetBtn.SetEnabled(false);
            headerRow.Add(m_ResetBtn);

            m_GraphSection.Add(headerRow);

            // ── BarGraphElement ──
            m_BarGraph = new BarGraphElement();
            m_BarGraph.AddToClassList("gc-alloc-bar-graph");
            m_BarGraph.style.flexGrow = 1;
            m_BarGraph.style.flexShrink = 1;
            m_BarGraph.style.minHeight = k_MinGraphHeight;

            // Load USS overrides
            var uss = Resources.Load<StyleSheet>("GCAllocAnalyzer");
            if (uss != null) m_BarGraph.styleSheets.Add(uss);

            // Apply bar spacing from EditorPrefs
            m_BarGraph.SetBarSpacingRatio(GCAllocSettings.GraphBarSpacing);

            // Configure settings
            m_BarGraph.Settings.EnableMouseZoomX = true;
            m_BarGraph.Settings.EnableMouseZoomY = true;
            m_BarGraph.Settings.EnableMousePan = true;
            m_BarGraph.Settings.EnableYPan = true;
            m_BarGraph.Settings.EnableSelection = true;
            m_BarGraph.Settings.ShowSegmentHighlightInLod = true;
            m_BarGraph.VisTagHighlightTint    = GCAllocSettings.GraphHighlightTint;
            m_BarGraph.VisTagHighlightOutline = GCAllocSettings.GraphHighlightOutline;

            // Input handlers
            m_BarGraph.SetInputSource(new BarGraphUIToolkitInput());
            m_BarGraph.AddHandler(new BarGraphHoverHandler());
            var selHandler = new ProfilerSelectionHandler();
            m_BarGraph.AddHandler(selHandler);
            m_BarGraph.AddHandler(new BarGraphPanHandler());
            m_BarGraph.AddHandler(new BarGraphZoomHandler());
            m_BarGraph.AddHandler(new BarGraphKeyboardNavigationHandler());
            var kbHandler = new ProfilerKeyboardHandler();
            m_BarGraph.AddHandler(kbHandler);
            m_BarGraph.AddHandler(new BarGraphYAxisDragHandler());
            m_BarGraph.AddHandler(new BarGraphScrollbarHandler());

            // Y-axis label formatter
            m_BarGraph.FormatYLabel = v => GCAllocUtils.FormatBytes((long)v);

            // Subscribe to handler events (input interpretation)
            selHandler.BarClicked += OnBarClicked;
            selHandler.SegmentClicked += OnSegmentClicked;
            selHandler.DragCompleted += OnDragCompletedInternal;
            kbHandler.BarClicked += OnBarClicked;
            kbHandler.SegmentClicked += OnSegmentClicked;

            // Subscribe to element events (state notifications)
            m_BarGraph.SelectionChanged += OnSelectionChangedInternal;
            m_BarGraph.HoverChanged += OnHoverChanged;
            m_BarGraph.SegmentHoverChanged += OnSegmentHoverChanged;
            m_BarGraph.ViewChanged += OnViewChanged;

            // Context menu
            m_BarGraph.AddManipulator(new ContextualMenuManipulator(OnGraphContextMenu));

            // Focus on hover for keyboard input
            m_BarGraph.focusable = true;
            m_BarGraph.RegisterCallback<PointerEnterEvent>(evt => m_BarGraph.Focus());
            m_BarGraph.RegisterCallback<PointerMoveEvent>(evt => m_LastPointerLocalPos = evt.localPosition);

            // ── Overview strip ──
            m_OverviewStrip = new BarGraphOverviewStrip
            {
                style = { height = k_OverviewHeight, marginBottom = 2 }
            };
            m_OverviewStrip.BindTo(m_BarGraph);

            m_GraphSection.Add(m_OverviewStrip);
            m_GraphSection.Add(m_BarGraph);

            // ── Floating tooltip ──
            m_FloatingTooltip = new Label
            {
                pickingMode = PickingMode.Ignore,
                style =
                {
                    position = Position.Absolute,
                    backgroundColor = k_TooltipBg,
                    color = Color.white,
                    fontSize = 11,
                    paddingLeft = 4, paddingRight = 4,
                    paddingTop = 2, paddingBottom = 2,
                    borderTopLeftRadius = 2, borderTopRightRadius = 2,
                    borderBottomLeftRadius = 2, borderBottomRightRadius = 2,
                    borderTopWidth = 1, borderBottomWidth = 1,
                    borderLeftWidth = 1, borderRightWidth = 1,
                    borderTopColor = k_TooltipBorder,
                    borderBottomColor = k_TooltipBorder,
                    borderLeftColor = k_TooltipBorder,
                    borderRightColor = k_TooltipBorder,
                    display = DisplayStyle.None
                }
            };

            // ── X-axis labels ──
            var xAxis = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    justifyContent = Justify.SpaceBetween,
                    height = k_GraphXAxisHeight
                }
            };
            m_GraphXStart = new Label("—")
            {
                style = { fontSize = 10, color = k_DimGray, flexShrink = 0 }
            };
            m_GraphOverlayLabel = new Label("")
            {
                style =
                {
                    flexGrow = 1, flexShrink = 1,
                    fontSize = 10, color = k_DimGray,
                    unityTextAlign = TextAnchor.MiddleCenter,
                    overflow = Overflow.Hidden,
                    textOverflow = TextOverflow.Ellipsis,
                    whiteSpace = WhiteSpace.NoWrap,
                    marginLeft = 8, marginRight = 8
                }
            };
            m_GraphXEnd = new Label("—")
            {
                style = { fontSize = 10, color = k_DimGray, flexShrink = 0 }
            };
            xAxis.Add(m_GraphXStart);
            xAxis.Add(m_GraphOverlayLabel);
            xAxis.Add(m_GraphXEnd);

            m_GraphSection.Add(xAxis);
        }

        // ═══════════════════════════════════════════════════
        //  PUBLIC API — DATA
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Store data references after each analysis run or Pull Data.
        /// </summary>
        public void SetData(GraphFrameStore frameStore, AnalysisSnapshot snapshot,
            List<CallsiteGroup> filteredGroups, bool groupByCallsite)
        {
            m_FrameStore = frameStore;
            m_Snapshot = snapshot;
            m_FilteredGroups = filteredGroups;
            m_GroupByCallsite = groupByCallsite;

            if (m_FrameStore != null && m_FrameStore.HasCachedAnalysis
                && m_FrameStore.HasFullFrameData)
            {
                var sw = Stopwatch.StartNew();
                m_MethodPalette.Build(m_FrameStore.CachedRawAllocations);
                m_LastMsPalette = sw.ElapsedMilliseconds;
                BuildSegmentData();
                m_LastMsSegments = sw.ElapsedMilliseconds - m_LastMsPalette;
                sw.Stop();
            }
            else
            {
                m_MethodPalette.Clear();
                m_HasSegmentData = false;
            }
        }

        /// <summary>
        /// Lightweight update for sub-range re-analysis.
        /// </summary>
        public void UpdateAnalyzedRange(AnalysisSnapshot snapshot,
            List<CallsiteGroup> filteredGroups, bool groupByCallsite)
        {
            m_Snapshot = snapshot;
            m_FilteredGroups = filteredGroups;
            m_GroupByCallsite = groupByCallsite;
        }

        // ═══════════════════════════════════════════════════
        //  REBUILD GRAPH
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Re-read settings (colors, spacing) and refresh.
        /// Called when EditorPrefs change via the Preferences panel.
        /// The main graph uses BarVisualProvider (reads colors live),
        /// but the overview strip uses baked segment colors, so we
        /// rebuild bar entries and re-push data without recomputing overlays.
        /// </summary>
        public void ApplySettings()
        {
            m_BarGraph.SetBarSpacingRatio(GCAllocSettings.GraphBarSpacing);
            m_BarGraph.VisTagHighlightTint    = GCAllocSettings.GraphHighlightTint;
            m_BarGraph.VisTagHighlightOutline = GCAllocSettings.GraphHighlightOutline;
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData) return;

            Color32 newBar = GCAllocSettings.GraphBarColor;
            Color32 newDim = GCAllocSettings.GraphDimColor;
            if (newBar.Equals(m_LastBakedBarColor) && newDim.Equals(m_LastBakedDimColor)) return;

            BuildBarEntries();
            m_BarGraph.SetData(m_BarEntries, m_BarEntryCount, m_BarSegments, m_BarSegmentCount);
            m_OverviewStrip.SetData(m_BarEntries, m_BarEntryCount, m_BarSegments, m_BarSegmentCount);
        }

        /// <summary>
        /// Rebuild the entire graph from current frame store data.
        /// </summary>
        public void RebuildGraph()
        {
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData)
            {
                m_GraphSection.style.display = DisplayStyle.None;
                return;
            }

            m_GraphSection.style.display = DisplayStyle.Flex;

            // Build bar entries with baked colors
            var sw = Stopwatch.StartNew();
            BuildBarEntries();
            long msBarEntries = sw.ElapsedMilliseconds;

            // Push data to chart and overview
            m_BarGraph.SetData(m_BarEntries, m_BarEntryCount, m_BarSegments, m_BarSegmentCount);
            m_OverviewStrip.SetData(m_BarEntries, m_BarEntryCount, m_BarSegments, m_BarSegmentCount);
            long msPush = sw.ElapsedMilliseconds - msBarEntries;

            // Allow zooming down to ~5 visible bars regardless of frame count
            m_BarGraph.ViewState.MaxZoomX = Mathf.Max(50f, m_BarEntryCount / 5f);

            // LOD color: use GraphBarColor for analyzed bars, GraphDimColor for unanalyzed
            m_BarGraph.BarVisualProvider = GetBarVisualOverride;
            m_BarGraph.TagHighlightFilter = IsBarAnalyzed;
            m_OverviewStrip.BarVisualProvider = GetOverviewBarVisual;

            // Update X-axis labels
            if (m_BarEntryCount > 0)
            {
                int firstFrame = m_FrameStore.FullFrameStart;
                int lastFrame = m_FrameStore.FullFrameEnd;
                m_GraphXStart.text = GCAllocUtils.DisplayFrame(firstFrame).ToString();
                m_GraphXEnd.text = GCAllocUtils.DisplayFrame(lastFrame).ToString();
            }

            // Y-axis reset button
            m_YAxisResetBtn.SetEnabled(m_BarGraph.ViewState.ZoomY > 1.01f);

            // Reset tooltip cache
            m_LastTooltipBar = -1;
            HideFloatingTooltip();

            // Re-apply overlay for currently selected marker
            int selectedIdx = m_GetSelectedMarkerIndex != null ? m_GetSelectedMarkerIndex() : -1;
            if (m_FilteredGroups != null && selectedIdx >= 0 && selectedIdx < m_FilteredGroups.Count)
                UpdateOverlay(m_FilteredGroups[selectedIdx]);
            else
                ClearOverlay();

            // Update button states
            UpdateResetButtonVisibility();

            sw.Stop();
            m_LogSB.Clear();
            m_LogSB.Append("[GCAllocAnalyzer] Graph: palette ");
            m_LogSB.Append(m_LastMsPalette);
            m_LogSB.Append("ms | segments ");
            m_LogSB.Append(m_LastMsSegments);
            m_LogSB.Append("ms | barEntries ");
            m_LogSB.Append(msBarEntries);
            m_LogSB.Append("ms | push ");
            m_LogSB.Append(msPush);
            m_LogSB.Append("ms | total ");
            m_LogSB.Append(m_LastMsPalette + m_LastMsSegments + sw.ElapsedMilliseconds);
            m_LogSB.Append("ms");
            UnityEngine.Debug.Log(m_LogSB.ToString());
            m_LastMsPalette = 0;
            m_LastMsSegments = 0;
        }

        // ═══════════════════════════════════════════════════
        //  BAR VISUAL PROVIDER
        // ═══════════════════════════════════════════════════

        bool IsBarAnalyzed(int barIndex)
        {
            if (!m_HasSelection) return true;
            return m_SelectionBuffer != null && barIndex >= 0
                && barIndex < m_SelectionBuffer.Length && m_SelectionBuffer[barIndex];
        }

        BarVisualOverride? GetBarVisualOverride(int barIndex)
        {
            if (!m_HasSelection || barIndex < 0
                || m_SelectionBuffer == null || barIndex >= m_SelectionBuffer.Length)
                return new BarVisualOverride { LodColor = GCAllocSettings.GraphBarColor };

            Color32 lodColor = m_SelectionBuffer[barIndex]
                ? GCAllocSettings.GraphBarColor
                : GCAllocSettings.GraphDimColor;

            return new BarVisualOverride { LodColor = lodColor };
        }

        BarVisualOverride? GetOverviewBarVisual(int barIndex)
        {
            if (!m_HasSelection || barIndex < 0
                || m_SelectionBuffer == null || barIndex >= m_SelectionBuffer.Length)
                return new BarVisualOverride { Color = GCAllocSettings.GraphBarColor };

            Color32 color = m_SelectionBuffer[barIndex]
                ? GCAllocSettings.GraphBarColor
                : GCAllocSettings.GraphDimColor;

            return new BarVisualOverride { Color = color };
        }

        // ═══════════════════════════════════════════════════
        //  DATA TRANSFORMS
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Build BarEntry[] and BarSegment[] from frame store data.
        /// One bar per frame, no bucketing. Colors are baked for analyzed-range dimming.
        /// </summary>
        void BuildBarEntries()
        {
            var perFrame = m_FrameStore.FullFrameBytes;
            int frameCount = perFrame.Length;
            int baseFrame = m_FrameStore.FullFrameStart;

            if (m_HasSegmentData)
                BuildBarEntriesWithSegments(perFrame, frameCount, baseFrame, !m_HasSelection);
            else
                BuildBarEntriesFlat(perFrame, frameCount);

            m_LastBakedBarColor = GCAllocSettings.GraphBarColor;
            m_LastBakedDimColor = GCAllocSettings.GraphDimColor;
        }

        void BuildBarEntriesFlat(long[] perFrame, int frameCount)
        {
            if (m_BarEntries == null || m_BarEntries.Length < frameCount)
                m_BarEntries = new BarEntry[frameCount];
            if (m_BarSegments == null || m_BarSegments.Length < frameCount)
                m_BarSegments = new BarSegment[frameCount];

            m_BarEntryCount = frameCount;
            m_BarSegmentCount = frameCount;

            Color32 barColor = GCAllocSettings.GraphBarColor;
            for (int i = 0; i < frameCount; i++)
            {
                float value = perFrame[i];
                m_BarSegments[i] = new BarSegment(value, barColor);
                m_BarEntries[i] = new BarEntry(i, 1, value);
            }
        }

        void BuildBarEntriesWithSegments(long[] perFrame, int frameCount, int baseFrame, bool isFullRange)
        {
            int methodCount = m_MethodPalette.Count;

            // Estimate segments: reuse m_SegmentFlatArray built by BuildSegmentData
            int estimatedSegs = frameCount * 12;
            if (m_BarEntries == null || m_BarEntries.Length < frameCount)
                m_BarEntries = new BarEntry[frameCount];
            if (m_BarSegments == null || m_BarSegments.Length < estimatedSegs)
                m_BarSegments = new BarSegment[estimatedSegs];

            // Reuse sort buffers
            if (m_SegmentSortIndices == null || m_SegmentSortIndices.Length < methodCount)
            {
                m_SegmentSortIndices = new int[Mathf.Max(methodCount, 64)];
                m_SegmentSortValues = new long[Mathf.Max(methodCount, 64)];
            }

            m_BarEntryCount = frameCount;
            int segIdx = 0;

            Color32 barColor = GCAllocSettings.GraphBarColor;
            Color32 dimColor = GCAllocSettings.GraphDimColor;

            for (int b = 0; b < frameCount; b++)
            {
                bool isAnalyzed = isFullRange
                    || (m_SelectionBuffer != null && b < m_SelectionBuffer.Length && m_SelectionBuffer[b]);

                int rowStart = b * methodCount;

                // Collect non-zero methods for this frame
                int nonZero = 0;
                for (int m = 0; m < methodCount; m++)
                {
                    long val = m_SegmentFlatArray[rowStart + m];
                    if (val > 0)
                    {
                        m_SegmentSortIndices[nonZero] = m;
                        m_SegmentSortValues[nonZero] = val;
                        nonZero++;
                    }
                }

                int segStart = segIdx;

                if (nonZero == 0)
                {
                    float value = perFrame[b];
                    if (segIdx >= m_BarSegments.Length)
                        GrowSegmentArray(ref m_BarSegments, segIdx + 1);

                    m_BarSegments[segIdx++] = new BarSegment(value, isAnalyzed ? barColor : dimColor);
                    m_BarEntries[b] = new BarEntry(segStart, 1, value);
                    continue;
                }

                // Sort ascending by bytes (smallest at bottom of stacked bar)
                for (int i = 1; i < nonZero; i++)
                {
                    long keyVal = m_SegmentSortValues[i];
                    int keyIdx = m_SegmentSortIndices[i];
                    int j = i - 1;
                    while (j >= 0 && m_SegmentSortValues[j] > keyVal)
                    {
                        m_SegmentSortValues[j + 1] = m_SegmentSortValues[j];
                        m_SegmentSortIndices[j + 1] = m_SegmentSortIndices[j];
                        j--;
                    }
                    m_SegmentSortValues[j + 1] = keyVal;
                    m_SegmentSortIndices[j + 1] = keyIdx;
                }

                // Emit segments
                if (segIdx + nonZero > m_BarSegments.Length)
                    GrowSegmentArray(ref m_BarSegments, segIdx + nonZero);

                float total = 0f;
                for (int m = 0; m < nonZero; m++)
                {
                    int methodIdx = m_SegmentSortIndices[m];
                    float val = m_SegmentSortValues[m];
                    total += val;

                    Color32 segColor = isAnalyzed
                        ? (Color32)m_MethodPalette.GetColor(methodIdx)
                        : dimColor;
                    m_BarSegments[segIdx++] = new BarSegment(val, segColor, methodIdx);
                }

                m_BarEntries[b] = new BarEntry(segStart, nonZero, total);
            }

            m_BarSegmentCount = segIdx;
        }

        static void GrowSegmentArray(ref BarSegment[] arr, int minSize)
        {
            var grown = new BarSegment[Mathf.Max(arr.Length * 2, minSize)];
            Array.Copy(arr, grown, arr.Length);
            arr = grown;
        }


        /// <summary>
        /// Build the flat accumulator array from cached raw allocations.
        /// Called once in SetData; the array is reused in BuildBarEntriesWithSegments.
        /// </summary>
        void BuildSegmentData()
        {
            var allocs = m_FrameStore.CachedRawAllocations;
            int frameCount = m_FrameStore.FullFrameBytes.Length;
            int methodCount = m_MethodPalette.Count;
            int baseFrame = m_FrameStore.FullFrameStart;

            int flatLen = frameCount * methodCount;
            if (m_SegmentFlatArray == null || m_SegmentFlatArray.Length < flatLen)
                m_SegmentFlatArray = new long[Mathf.Max(flatLen, 256)];
            else
                Array.Clear(m_SegmentFlatArray, 0, flatLen);

            for (int a = 0; a < allocs.Count; a++)
            {
                int methodIdx = allocs[a].SegmentMethodIndex;
                if (methodIdx < 0) continue;
                int frameRel = allocs[a].FrameIndex - baseFrame;
                m_SegmentFlatArray[frameRel * methodCount + methodIdx] += allocs[a].Bytes;
            }

            m_HasSegmentData = true;
        }

        /// <summary>
        /// Build per-bar overlay values for method highlight.
        /// Each value is the proportional contribution of the highlighted group.
        /// </summary>
        void BuildOverlayValues(CallsiteGroup group)
        {
            if (m_Snapshot == null || !m_Snapshot.HasRawAllocations) return;
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData) return;

            int fullFrameCount = m_FrameStore.FullFrameBytes.Length;
            int snapshotFrameCount = m_Snapshot.PerFrameBytes != null ? m_Snapshot.PerFrameBytes.Length : 0;
            if (snapshotFrameCount == 0) return;

            // Sum group's allocations into per-frame buffer
            EnsurePerFrameBuffer(snapshotFrameCount);
            int groupIdx = group.GroupIndex;
            var allocs = m_Snapshot.RawAllocations;
            for (int i = 0; i < allocs.Count; i++)
            {
                var alloc = allocs[i];
                int matchIdx = m_GroupByCallsite ? alloc.FullCallstackGroupIndex : alloc.TopFrameGroupIndex;
                if (matchIdx != groupIdx) continue;
                int idx = alloc.FrameIndex - m_Snapshot.FrameStart;
                if (idx >= 0 && idx < snapshotFrameCount)
                    m_PerFrameBuffer[idx] += alloc.Bytes;
            }

            // Map per-snapshot-frame values to per-bar values
            if (m_OverlayValues == null || m_OverlayValues.Length < fullFrameCount)
                m_OverlayValues = new float[fullFrameCount];

            for (int i = 0; i < fullFrameCount; i++)
            {
                int snapshotIdx = (m_FrameStore.FullFrameStart + i) - m_Snapshot.FrameStart;
                m_OverlayValues[i] = (snapshotIdx >= 0 && snapshotIdx < snapshotFrameCount)
                    ? m_PerFrameBuffer[snapshotIdx]
                    : 0f;
            }
        }

        void EnsurePerFrameBuffer(int size)
        {
            if (m_PerFrameBuffer == null || m_PerFrameBuffer.Length < size)
                m_PerFrameBuffer = new long[size];
            else
                Array.Clear(m_PerFrameBuffer, 0, size);
        }

        // ═══════════════════════════════════════════════════
        //  OVERLAY & METHOD HIGHLIGHT
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Highlight segments matching a specific callsite group's method.
        /// </summary>
        public void UpdateOverlay(CallsiteGroup group)
        {
            if (group == null)
            {
                ClearOverlay();
                return;
            }

            // Overlay bars — visible in LOD mode when segments aren't rendered
            BuildOverlayValues(group);
            if (m_OverlayValues != null)
                m_BarGraph.SetOverlay(m_OverlayValues, GCAllocSettings.GraphOverlayColor);

            // Segment-level tag highlight — visible when zoomed in with segments
            int tag = m_MethodPalette.GetIndex(group.DisplayName);
            if (tag >= 0)
                m_BarGraph.HighlightTag(tag);
            else
                m_BarGraph.ClearTagHighlight();

            m_GraphOverlayLabel.text = group.DisplayName;
        }

        public void ClearOverlay()
        {
            m_BarGraph.ClearOverlay();
            m_BarGraph.ClearTagHighlight();
            m_GraphOverlayLabel.text = "";
        }

        // ═══════════════════════════════════════════════════
        //  SELECTION
        // ═══════════════════════════════════════════════════

        public void ClearSelection()
        {
            m_BarGraph.ClearSelection();
            m_HighlightedFrame = -1;
            m_HasSelection = false;
        }

        public void ResetToFullRange()
        {
            ClearSelection();

            var snap = m_BarGraph.CreateViewSnapshot();
            snap.ZoomX = 1f;
            snap.ZoomY = 1f;
            snap.PanX = 0f;
            snap.PanY = 0f;
            m_BarGraph.RestoreViewSnapshot(snap);
        }

        /// <summary>
        /// Derive frame indices from the current SelectedBars.
        /// Returns false if no selection exists.
        /// </summary>
        bool GetSelectedFrameRange(out int startFrame, out int endFrame)
        {
            var selected = m_BarGraph.ViewState.SelectedBars;
            if (selected.Count == 0)
            {
                startFrame = -1;
                endFrame = -1;
                return false;
            }

            int baseFrame = m_FrameStore.FullFrameStart;
            int minFrame = int.MaxValue;
            int maxFrame = int.MinValue;
            foreach (int barIdx in selected)
            {
                int frame = baseFrame + barIdx;
                if (frame < minFrame) minFrame = frame;
                if (frame > maxFrame) maxFrame = frame;
            }
            startFrame = minFrame;
            endFrame = maxFrame;
            return true;
        }

        /// <summary>
        /// Build a bool[] frame selection buffer from SelectedBars.
        /// Used by the main window for "Analyze Selection".
        /// </summary>
        public bool GetSelectedFrameBuffer(out bool[] buffer, out int baseFrame)
        {
            var selected = m_BarGraph.ViewState.SelectedBars;
            if (selected.Count == 0 || m_FrameStore == null)
            {
                buffer = null;
                baseFrame = 0;
                return false;
            }

            int fullFrameCount = m_FrameStore.FullFrameBytes.Length;
            baseFrame = m_FrameStore.FullFrameStart;

            buffer = new bool[fullFrameCount];
            foreach (int barIdx in selected)
            {
                if (barIdx >= 0 && barIdx < fullFrameCount)
                    buffer[barIdx] = true;
            }
            return true;
        }

        /// <summary>True if there is an active drag selection.</summary>
        public bool HasFrameSelection => m_BarGraph != null && m_BarGraph.ViewState.SelectedBars.Count > 0;

        // ═══════════════════════════════════════════════════
        //  SERIALIZATION
        // ═══════════════════════════════════════════════════

        public BarGraphViewSnapshot CaptureState()
        {
            return m_BarGraph.CreateViewSnapshot();
        }

        public void RestoreState(BarGraphViewSnapshot state)
        {
            if (!state.IsValid) return;
            m_BarGraph.RestoreViewSnapshot(state);
            UpdateSortToggleLabel();
            m_YAxisResetBtn.SetEnabled(m_BarGraph.ViewState.ZoomY > 1.01f);
        }

        /// <summary>
        /// Re-apply segment selection after RebuildGraph, which calls SetData
        /// and clears all selection state via OnDataChanged.
        /// </summary>
        public void RestoreSegmentSelection(BarGraphViewSnapshot state)
        {
            if (!state.IsValid) return;
            if (state.SelectedSegmentBar >= 0 && state.SelectedSegmentBar < m_BarEntryCount)
                m_BarGraph.SelectSegment(state.SelectedSegmentBar, state.SelectedSegmentIndex);
        }

        // ═══════════════════════════════════════════════════
        //  EVENT HANDLERS — BAR GRAPH
        // ═══════════════════════════════════════════════════

        void OnBarClicked(BarClickedEventArgs args)
        {
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData) return;

            int frameIndex = m_FrameStore.FullFrameStart + args.DataIndex;
            m_HighlightedFrame = frameIndex;
            m_BarGraph.ClearTagHighlight();

            string methodName = null;
            if (m_HasSegmentData && args.DataIndex < m_BarEntryCount)
            {
                ref readonly BarEntry bar = ref m_BarEntries[args.DataIndex];
                if (bar.SegmentCount > 1)
                {
                    int bestSeg = 0;
                    float bestVal = 0f;
                    for (int s = 0; s < bar.SegmentCount; s++)
                    {
                        float v = m_BarSegments[bar.SegmentStart + s].Value;
                        if (v > bestVal) { bestVal = v; bestSeg = s; }
                    }
                    m_BarGraph.SelectSegment(args.DataIndex, bestSeg);
                    int tag = m_BarSegments[bar.SegmentStart + bestSeg].Tag;
                    if (tag >= 0) methodName = m_MethodPalette.GetMethodName(tag);
                }
                else if (bar.SegmentCount == 1)
                {
                    m_BarGraph.SelectSegment(args.DataIndex, 0);
                    int tag = m_BarSegments[bar.SegmentStart].Tag;
                    if (tag >= 0) methodName = m_MethodPalette.GetMethodName(tag);
                }
            }

            m_OnFrameSelected?.Invoke(frameIndex, methodName);
        }

        void OnSegmentClicked(SegmentEventArgs args)
        {
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData) return;

            int frameIndex = m_FrameStore.FullFrameStart + args.BarDataIndex;
            m_HighlightedFrame = frameIndex;

            if (args.Tag >= 0)
                m_BarGraph.HighlightTag(args.Tag);
            else
                m_BarGraph.ClearTagHighlight();

            string methodName = args.Tag >= 0 ? m_MethodPalette.GetMethodName(args.Tag) : null;
            m_OnFrameSelected?.Invoke(frameIndex, methodName);
        }

        void OnSelectionChangedInternal(SelectionChangedEventArgs args)
        {
            if (!GetSelectedFrameRange(out int startFrame, out int endFrame)) return;
            OnSelectionChanged?.Invoke(startFrame, endFrame);
        }

        void OnDragCompletedInternal(DragCompletedEventArgs args)
        {
            m_BarGraph.ClearSegmentSelection();
            PopulateSelectionBuffer();
            m_OverviewStrip.BarVisualProvider = GetOverviewBarVisual;
            if (!GetSelectedFrameRange(out int startFrame, out int endFrame)) return;
            OnDragCompleted?.Invoke(startFrame, endFrame);
        }

        void PopulateSelectionBuffer()
        {
            int count = m_BarEntryCount;
            if (m_SelectionBuffer == null || m_SelectionBuffer.Length < count)
                m_SelectionBuffer = new bool[count];
            else
                Array.Clear(m_SelectionBuffer, 0, count);

            var selected = m_BarGraph.ViewState.SelectedBars;
            foreach (int idx in selected)
                if (idx >= 0 && idx < count)
                    m_SelectionBuffer[idx] = true;

            m_HasSelection = true;
        }

        void OnHoverChanged(HoverChangedEventArgs args)
        {
            if (args.DataIndex < 0)
            {
                m_LastTooltipBar = -1;
                HideFloatingTooltip();
                return;
            }

            m_LastTooltipBar = args.DataIndex;
            int frameIndex = m_FrameStore.FullFrameStart + args.DataIndex;
            long val = (long)args.TotalValue;

            // Position tooltip at the bar's approximate location
            ShowFloatingTooltip(string.Concat(
                "Frame ", GCAllocUtils.DisplayFrame(frameIndex).ToString(),
                ": ", GCAllocUtils.FormatBytes(val)));
        }

        void OnSegmentHoverChanged(SegmentEventArgs args)
        {
            if (args.BarDataIndex < 0)
            {
                m_LastTooltipBar = -1;
                HideFloatingTooltip();
                return;
            }

            m_LastTooltipBar = args.BarDataIndex;
            string methodName = args.Tag >= 0
                ? m_MethodPalette.GetMethodName(args.Tag)
                : "Others";
            long segBytes = (long)args.Value;

            int frameIndex = m_FrameStore.FullFrameStart + args.BarDataIndex;
            long totalForFrame = m_FrameStore.FullFrameBytes[args.BarDataIndex];
            int pct = totalForFrame > 0 ? (int)(segBytes * 100 / totalForFrame) : 0;

            ShowFloatingTooltip(string.Concat(
                methodName.Replace("  —  ", "\n— "),
                ": ", GCAllocUtils.FormatBytes(segBytes),
                " (", pct.ToString(), "%)"));
        }

        void OnViewChanged(ViewChangedEventArgs args)
        {
            // Update Y-axis reset button when zoom changes
            m_YAxisResetBtn.SetEnabled(args.ZoomY > 1.01f);
        }

        // ═══════════════════════════════════════════════════
        //  CONTEXT MENU
        // ═══════════════════════════════════════════════════

        void OnGraphContextMenu(ContextualMenuPopulateEvent evt)
        {
            bool hasData = m_BarEntryCount > 0;
            bool hasSelection = m_BarGraph.ViewState.SelectedBars.Count > 0;

            evt.menu.AppendAction("Select All", OnContextSelectAll,
                hasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendAction("Clear Selection", OnContextClearSelection,
                hasSelection ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendSeparator();

            evt.menu.AppendAction("Select Frame with Max GC", OnContextSelectMaxGC,
                hasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendAction("Analyze Selection", OnContextAnalyzeSelection,
                hasSelection ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendSeparator();

            evt.menu.AppendAction("Reset Zoom", OnContextResetZoom,
                IsZoomedIn ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendAction(
                OrderByMagnitude ? "Order by Frame" : "Order by Size",
                OnContextToggleSort,
                hasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        void OnContextSelectAll(DropdownMenuAction _)
        {
            m_BarGraph.SelectAll();
        }

        void OnContextClearSelection(DropdownMenuAction _) => ClearSelection();

        void OnContextSelectMaxGC(DropdownMenuAction _) => SelectExtremeFrame(true);

        void OnContextAnalyzeSelection(DropdownMenuAction _)
        {
            if (!GetSelectedFrameRange(out int sf, out int ef)) return;
            OnDragCompleted?.Invoke(sf, ef);
        }

        void OnContextResetZoom(DropdownMenuAction _)
        {
            var snap = m_BarGraph.CreateViewSnapshot();
            snap.ZoomX = 1f;
            snap.ZoomY = 1f;
            snap.PanX = 0f;
            snap.PanY = 0f;
            m_BarGraph.RestoreViewSnapshot(snap);
        }

        void OnContextToggleSort(DropdownMenuAction _) => OnSortToggleClicked();

        // ═══════════════════════════════════════════════════
        //  SELECT EXTREME FRAME
        // ═══════════════════════════════════════════════════

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

            m_BarGraph.SelectBar(extremeIdx);

            int frameIndex = m_FrameStore.FullFrameStart + extremeIdx;
            m_HighlightedFrame = frameIndex;

            m_OnFrameSelected?.Invoke(frameIndex, null);
        }

        // ═══════════════════════════════════════════════════
        //  BUTTON HANDLERS
        // ═══════════════════════════════════════════════════

        void OnSortToggleClicked()
        {
            OrderByMagnitude = !OrderByMagnitude;
        }

        void UpdateSortToggleLabel()
        {
            if (m_SortToggleBtn == null) return;
            m_SortToggleBtn.text = OrderByMagnitude ? "Order by Frame" : "Order by Size";
        }

        void ResetYAxisScale()
        {
            var snap = m_BarGraph.CreateViewSnapshot();
            snap.ZoomY = 1f;
            snap.PanY = 0f;
            m_BarGraph.RestoreViewSnapshot(snap);
            m_YAxisResetBtn.SetEnabled(false);
        }

        void UpdateResetButtonVisibility()
        {
            if (m_ResetBtn == null || m_FrameStore == null) return;

            bool isSubRange = m_HasSelection && m_FrameStore.HasCachedAnalysis;

            m_ResetBtn.SetEnabled(isSubRange);
        }

        // ═══════════════════════════════════════════════════
        //  TOOLTIP
        // ═══════════════════════════════════════════════════

        void ShowFloatingTooltip(string text)
        {
            m_FloatingTooltip.text = text;
            m_FloatingTooltip.style.display = DisplayStyle.Flex;
            m_FloatingTooltip.BringToFront();

            var worldPos = m_BarGraph.LocalToWorld(m_LastPointerLocalPos);
            var tooltipParent = m_FloatingTooltip.parent;
            var pos = tooltipParent != null ? tooltipParent.WorldToLocal(worldPos) : worldPos;

            float x = pos.x + 12f;
            float y = pos.y - 28f;
            if (y < 0f) y = pos.y + 16f;

            m_FloatingTooltip.style.left = x;
            m_FloatingTooltip.style.top = y;
        }

        void HideFloatingTooltip()
        {
            m_FloatingTooltip.style.display = DisplayStyle.None;
        }
    }
}
