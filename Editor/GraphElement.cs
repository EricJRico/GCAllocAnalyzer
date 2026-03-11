using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace GCAllocBreakdown.Editor
{
    // ═══════════════════════════════════════════════════
    //  DATA STRUCTS
    // ═══════════════════════════════════════════════════

    internal struct BarData
    {
        public int StartFrame;   // first frame index in this bar
        public int EndFrame;     // last frame index in this bar
        public long Value;       // max bytes in this bar's frame range
    }

    internal struct GridLine
    {
        public float Y;          // Y position from bottom of graph area (computed against k_GraphHeight)
        public long Value;       // raw byte value for this grid line
        public string Label;     // e.g. "1 MB", "512 KB" (pre-computed by controller)
    }

    // ═══════════════════════════════════════════════════
    //  GRAPH ELEMENT — Custom Painter2D-based bar graph
    //  Replaces one-VisualElement-per-bar approach with
    //  a single element that draws all bars via
    //  generateVisualContent / Painter2D.
    // ═══════════════════════════════════════════════════

    internal class GraphElement : VisualElement
    {
        // ═══════════════════════════════════════════════════
        //  CONSTANTS
        // ═══════════════════════════════════════════════════

        const float k_MinDragThreshold = 3f;

        // ═══════════════════════════════════════════════════
        //  COLORS
        // ═══════════════════════════════════════════════════

        static readonly Color k_Background       = new Color(0.18f, 0.18f, 0.18f);
        static readonly Color k_GridLineColor     = new Color(0.3f, 0.3f, 0.3f, 0.6f);
        static readonly Color k_BarNormal         = new Color(0.27f, 0.67f, 0.6f);
        static readonly Color k_SelectionAreaBarColor      = new Color(0.4f, 0.8f, 0.73f);
        static readonly Color k_SelectionAreaBgColor = new Color(0.25f, 0.35f, 0.55f, 0.3f);
        static readonly Color k_SelectionEdgeColor  = new Color(0.5f, 0.7f, 1f, 0.8f);
        static readonly Color k_HighlightedMethodOverlay  = new Color(1f, 1f, 1f, 0.6f);
        static readonly Color k_BarDimmed         = new Color(0.27f, 0.67f, 0.6f, 0.3f);
        static readonly Color k_HoverOverlay      = new Color(1f, 1f, 1f, 0.12f);

        // ═══════════════════════════════════════════════════
        //  BAR DATA
        // ═══════════════════════════════════════════════════

        BarData[] m_Bars;
        int m_BarOffset;
        int m_BarCount;
        float m_VisibleSpan; // exact number of bars the viewport covers (fractional)
        float m_FractionalOffset; // sub-bar offset for smooth scrolling [0, 1)

        BarData[] m_OverlayBars;
        int m_OverlayBarCount;

        GridLine[] m_GridLines;
        int m_GridLineCount;

        long m_YAxisMax;
        long m_YPanOffset;
        bool m_HasData;

        // ═══════════════════════════════════════════════════
        //  SELECTION STATE
        // ═══════════════════════════════════════════════════

        int m_SelectionStart = -1;
        int m_SelectionEnd   = -1;
        int m_HighlightedBar = -1;
        int m_HighlightedSegment = -1; // index into m_Segments; -1 = whole bar

        int m_AnalyzedStartBar = -1;
        int m_AnalyzedEndBar   = -1;
        bool[] m_AnalyzedBarMask; // per-bar mask for non-contiguous analyzed ranges

        // Stacked bar segment data
        BarSegment[] m_Segments;
        int[] m_SegmentOffsets;
        bool m_HasSegmentData;
        MethodColorPalette m_MethodPalette;
        int[] m_SegmentIndexMap; // display index → original index for segment lookup (null = identity)
        int m_HighlightedMethodIndex = -1; // method index to tint in stacked segments (-1 = none)

        // ═══════════════════════════════════════════════════
        //  DRAG STATE
        // ═══════════════════════════════════════════════════

        bool m_PointerDown;
        bool m_Dragging;
        Vector2 m_PointerDownPos;
        float m_DragStartX;
        float m_DragCurrentX;

        // ═══════════════════════════════════════════════════
        //  EVENTS
        // ═══════════════════════════════════════════════════

        public event Action<int, float> BarClicked;
        public event Action<int, int> SelectionChanged;
        public event Action<int, int> DragCompleted;
        // ═══════════════════════════════════════════════════
        //  PROPERTIES
        // ═══════════════════════════════════════════════════

        public long YAxisMax
        {
            get => m_YAxisMax;
            set
            {
                if (m_YAxisMax == value) return;
                m_YAxisMax = value;
                MarkDirtyRepaint();
            }
        }

        public long YPanOffset
        {
            get => m_YPanOffset;
            set
            {
                if (m_YPanOffset == value) return;
                m_YPanOffset = value;
                MarkDirtyRepaint();
            }
        }

        public bool HasData
        {
            get => m_HasData;
            set
            {
                if (m_HasData == value) return;
                m_HasData = value;
                MarkDirtyRepaint();
            }
        }

        // ═══════════════════════════════════════════════════
        //  CONSTRUCTOR
        // ═══════════════════════════════════════════════════

        public GraphElement()
        {
            focusable = true;
            style.flexGrow = 1;
            style.flexShrink = 1;
            style.overflow = Overflow.Hidden;

            generateVisualContent += OnGenerateVisualContent;

            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerLeaveEvent>(OnPointerLeave);
            RegisterCallback<KeyDownEvent>(OnKeyDown);
        }

        // ═══════════════════════════════════════════════════
        //  PUBLIC DATA SETTERS
        // ═══════════════════════════════════════════════════

        public void SetBarData(BarData[] bars, int offset, int count, float visibleSpan = 0f, float fractionalOffset = 0f)
        {
            m_Bars = bars;
            m_BarOffset = offset;
            m_BarCount = bars != null ? count : 0;
            m_VisibleSpan = visibleSpan > 0f ? visibleSpan : m_BarCount;
            m_FractionalOffset = fractionalOffset;
            MarkDirtyRepaint();
        }

        public void SetHighlightedMethod(int methodIndex)
        {
            if (m_HighlightedMethodIndex == methodIndex) return;
            m_HighlightedMethodIndex = methodIndex;
            MarkDirtyRepaint();
        }

        public void SetOverlayData(BarData[] overlayBars, int count)
        {
            m_OverlayBars = overlayBars;
            m_OverlayBarCount = overlayBars != null ? count : 0;
            MarkDirtyRepaint();
        }

        public void SetGridLines(GridLine[] lines, int count)
        {
            m_GridLines = lines;
            m_GridLineCount = lines != null ? count : 0;
            MarkDirtyRepaint();
        }

        public void SetSelection(int selectionStart, int selectionEnd)
        {
            if (m_SelectionStart == selectionStart && m_SelectionEnd == selectionEnd) return;
            m_SelectionStart = selectionStart;
            m_SelectionEnd = selectionEnd;
            MarkDirtyRepaint();
        }

        public void SetHighlightedBar(int barIndex, int segmentIndex = -1)
        {
            if (m_HighlightedBar == barIndex && m_HighlightedSegment == segmentIndex) return;
            m_HighlightedBar = barIndex;
            m_HighlightedSegment = segmentIndex;
            MarkDirtyRepaint();
        }

        public void SetAnalyzedRange(int startBar, int endBar)
        {
            if (m_AnalyzedStartBar == startBar && m_AnalyzedEndBar == endBar) return;
            m_AnalyzedStartBar = startBar;
            m_AnalyzedEndBar = endBar;
            MarkDirtyRepaint();
        }

        /// <summary>
        /// Set a per-bar mask for analyzed dimming. When set, mask[i] == true means
        /// bar i is in the analyzed range (full brightness); false means dimmed.
        /// Pass null to clear and fall back to the contiguous start/end range.
        /// </summary>
        public void SetAnalyzedMask(bool[] mask)
        {
            m_AnalyzedBarMask = mask;
            MarkDirtyRepaint();
        }

        public void SetSegmentData(BarSegment[] segments, int[] offsets, bool hasData, MethodColorPalette palette, int[] indexMap = null)
        {
            m_Segments = segments;
            m_SegmentOffsets = offsets;
            m_SegmentIndexMap = indexMap;
            m_HasSegmentData = hasData;
            m_MethodPalette = palette;
            MarkDirtyRepaint();
        }

        // ═══════════════════════════════════════════════════
        //  VISUAL CONTENT GENERATION
        // ═══════════════════════════════════════════════════

        void OnGenerateVisualContent(MeshGenerationContext mgc)
        {
            Rect rect = contentRect;
            if (rect.width < 1f || rect.height < 1f) return;

            var painter = mgc.painter2D;

            DrawBackground(painter, rect);
            DrawGridLines(painter, rect);
            DrawSelectionBackground(painter, rect);
            DrawBars(painter, rect);
            DrawOverlayBars(painter, rect);
            DrawSelectionEdges(painter, rect);
        }

        // ═══════════════════════════════════════════════════
        //  DRAWING — BACKGROUND
        // ═══════════════════════════════════════════════════

        void DrawBackground(Painter2D painter, Rect rect)
        {
            painter.fillColor = k_Background;
            painter.BeginPath();
            painter.MoveTo(new Vector2(0f, 0f));
            painter.LineTo(new Vector2(rect.width, 0f));
            painter.LineTo(new Vector2(rect.width, rect.height));
            painter.LineTo(new Vector2(0f, rect.height));
            painter.ClosePath();
            painter.Fill();
        }

        // ═══════════════════════════════════════════════════
        //  DRAWING — GRID LINES
        // ═══════════════════════════════════════════════════

        void DrawGridLines(Painter2D painter, Rect rect)
        {
            if (m_GridLines == null || m_GridLineCount <= 0) return;

            painter.strokeColor = k_GridLineColor;
            painter.lineWidth = 1f;

            for (int i = 0; i < m_GridLineCount; i++)
            {
                // Compute Y from raw value to match bar height scaling
                float fromBottom = m_YAxisMax > 0
                    ? (float)(m_GridLines[i].Value - m_YPanOffset) / m_YAxisMax * rect.height
                    : m_GridLines[i].Y;
                float y = rect.height - fromBottom;
                if (y < 0f || y > rect.height) continue;

                painter.BeginPath();
                painter.MoveTo(new Vector2(0f, y));
                painter.LineTo(new Vector2(rect.width, y));
                painter.Stroke();
            }
        }

        // ═══════════════════════════════════════════════════
        //  DRAWING — SELECTION BACKGROUND
        // ═══════════════════════════════════════════════════

        void DrawSelectionBackground(Painter2D painter, Rect rect)
        {
            float left, right;

            if (m_Dragging)
            {
                // During active drag, use raw pixel positions for immediate feedback
                left  = m_DragStartX < m_DragCurrentX ? m_DragStartX : m_DragCurrentX;
                right = m_DragStartX < m_DragCurrentX ? m_DragCurrentX : m_DragStartX;
                left  = Mathf.Clamp(left, 0f, rect.width);
                right = Mathf.Clamp(right, 0f, rect.width);
            }
            else
            {
                // Finalized selection — snap to bar boundaries
                if (m_SelectionStart < 0 || m_SelectionEnd < 0) return;
                if (m_Bars == null || m_BarCount <= 0) return;

                int sStart = m_SelectionStart < m_SelectionEnd ? m_SelectionStart : m_SelectionEnd;
                int sEnd   = m_SelectionStart < m_SelectionEnd ? m_SelectionEnd   : m_SelectionStart;

                if (sStart >= m_BarCount) return;
                if (sEnd >= m_BarCount) sEnd = m_BarCount - 1;

                float barWidth = rect.width / m_VisibleSpan;
                left  = (sStart - m_FractionalOffset) * barWidth;
                right = (sEnd + 1 - m_FractionalOffset) * barWidth;
            }

            if (right - left < 1f) return;

            painter.fillColor = k_SelectionAreaBgColor;
            painter.BeginPath();
            painter.MoveTo(new Vector2(left, 0f));
            painter.LineTo(new Vector2(right, 0f));
            painter.LineTo(new Vector2(right, rect.height));
            painter.LineTo(new Vector2(left, rect.height));
            painter.ClosePath();
            painter.Fill();
        }

        void DrawSelectionEdges(Painter2D painter, Rect rect)
        {
            float left, right;

            if (m_Dragging)
            {
                left  = Mathf.Clamp(m_DragStartX < m_DragCurrentX ? m_DragStartX : m_DragCurrentX, 0f, rect.width);
                right = Mathf.Clamp(m_DragStartX < m_DragCurrentX ? m_DragCurrentX : m_DragStartX, 0f, rect.width);
            }
            else
            {
                if (m_SelectionStart < 0 || m_SelectionEnd < 0) return;
                if (m_Bars == null || m_BarCount <= 0) return;

                int sStart = m_SelectionStart < m_SelectionEnd ? m_SelectionStart : m_SelectionEnd;
                int sEnd   = m_SelectionStart < m_SelectionEnd ? m_SelectionEnd   : m_SelectionStart;

                if (sStart >= m_BarCount) return;
                if (sEnd >= m_BarCount) sEnd = m_BarCount - 1;

                float barWidth = rect.width / m_VisibleSpan;
                left  = (sStart - m_FractionalOffset) * barWidth;
                right = (sEnd + 1 - m_FractionalOffset) * barWidth;
            }

            if (right - left < 1f) return;

            painter.strokeColor = k_SelectionEdgeColor;
            painter.lineWidth = 1f;
            painter.BeginPath();
            painter.MoveTo(new Vector2(left, 0f));
            painter.LineTo(new Vector2(left, rect.height));
            painter.Stroke();
            painter.BeginPath();
            painter.MoveTo(new Vector2(right, 0f));
            painter.LineTo(new Vector2(right, rect.height));
            painter.Stroke();
        }

        // ═══════════════════════════════════════════════════
        //  DRAWING — BARS
        // ═══════════════════════════════════════════════════

        void DrawBars(Painter2D painter, Rect rect)
        {
            if (m_Bars == null || m_BarCount <= 0) return;

            // Normalize selection range — during active drag, derive from pixel positions
            int sStart = -1, sEnd = -1;
            if (m_Dragging)
            {
                float leftX  = m_DragStartX < m_DragCurrentX ? m_DragStartX : m_DragCurrentX;
                float rightX = m_DragStartX < m_DragCurrentX ? m_DragCurrentX : m_DragStartX;
                sStart = HitTestBar(new Vector2(leftX, 0f));
                sEnd   = HitTestBar(new Vector2(rightX, 0f));
                if (sStart < 0) sStart = 0;
                if (sEnd < 0) sEnd = m_BarCount - 1;
            }
            else if (m_SelectionStart >= 0 && m_SelectionEnd >= 0)
            {
                sStart = m_SelectionStart < m_SelectionEnd ? m_SelectionStart : m_SelectionEnd;
                sEnd   = m_SelectionStart < m_SelectionEnd ? m_SelectionEnd   : m_SelectionStart;
            }

            float areaWidth = rect.width;
            float areaHeight = rect.height;
            float barWidth = areaWidth / m_VisibleSpan;

            for (int i = 0; i < m_BarCount; i++)
            {
                int srcIdx = m_BarOffset + i;

                long barValue = m_Bars[srcIdx].Value;
                if (barValue <= m_YPanOffset) continue;

                float barTop = m_YAxisMax > 0
                    ? areaHeight - (float)(barValue - m_YPanOffset) / m_YAxisMax * areaHeight
                    : areaHeight;
                float barBottom = m_YAxisMax > 0
                    ? areaHeight + (float)m_YPanOffset / m_YAxisMax * areaHeight
                    : areaHeight;
                float barHeight = barBottom - barTop;
                if (barHeight <= 0f) continue;

                // Determine bar state flags
                bool inAnalyzed;
                if (m_AnalyzedBarMask != null)
                    inAnalyzed = srcIdx < m_AnalyzedBarMask.Length && m_AnalyzedBarMask[srcIdx];
                else
                    inAnalyzed = m_AnalyzedStartBar == -1
                        || (m_AnalyzedStartBar >= 0 && i >= m_AnalyzedStartBar && i <= m_AnalyzedEndBar);

                bool isHighlighted = i == m_HighlightedBar;
                bool isSelected = sStart >= 0 && i >= sStart && i <= sEnd;

                float x = (i - m_FractionalOffset) * barWidth;
                float w = Mathf.Max(barWidth, 1f);

                // Check if this bar should use stacked rendering
                bool useStacked = false;
                int segStart = 0, segEnd = 0;
                int segLookup = m_SegmentIndexMap != null ? m_SegmentIndexMap[srcIdx] : srcIdx;
                if (m_HasSegmentData && inAnalyzed && m_SegmentOffsets != null
                    && segLookup + 1 < m_SegmentOffsets.Length)
                {
                    segStart = m_SegmentOffsets[segLookup];
                    segEnd = m_SegmentOffsets[segLookup + 1];

                    // Only use stacked rendering when 2+ segments are visually
                    // distinguishable at the current zoom level.
                    int visibleCount = 0;
                    for (int s = segStart; s < segEnd && visibleCount < 2; s++)
                    {
                        float segH = m_YAxisMax > 0
                            ? (float)m_Segments[s].Bytes / m_YAxisMax * areaHeight
                            : 0f;
                        if (segH >= 0.5f)
                            visibleCount++;
                    }
                    useStacked = visibleCount >= 2;
                }

                if (useStacked)
                {
                    // STACKED: multiple methods — draw colored segments
                    float currentY = m_YAxisMax > 0
                        ? areaHeight + (float)m_YPanOffset / m_YAxisMax * areaHeight
                        : areaHeight;
                    for (int s = segStart; s < segEnd; s++)
                    {
                        float segH = m_YAxisMax > 0
                            ? (float)m_Segments[s].Bytes / m_YAxisMax * areaHeight
                            : 0f;
                        float segTop = currentY - segH;

                        if (segH >= 0.5f)
                        {
                            Color segColor = m_MethodPalette.GetColor(m_Segments[s].MethodIndex);
                            segColor = ModulateSegmentColor(segColor, isSelected);

                            DrawFilledRect(painter, x, segTop, w, segH, segColor);

                            // Tint highlighted method (selected marker in list)
                            if (m_HighlightedMethodIndex >= 0 && m_Segments[s].MethodIndex == m_HighlightedMethodIndex)
                                DrawFilledRect(painter, x, segTop, w, segH, k_HighlightedMethodOverlay);

                            if (isHighlighted && s == m_HighlightedSegment)
                                DrawFilledRect(painter, x, segTop, w, segH, k_HoverOverlay);
                        }

                        currentY = segTop;
                    }
                }
                else
                {
                    // Solid bar (bucketed, un-analyzed, single method, or no segment data)
                    Color color;
                    if (isSelected)
                        color = k_SelectionAreaBarColor;
                    else if (!inAnalyzed)
                        color = k_BarDimmed;
                    else
                        color = k_BarNormal;

                    DrawFilledRect(painter, x, barTop, w, barHeight, color);

                    // Tint if this bar contains the highlighted method
                    if (m_HighlightedMethodIndex >= 0 && inAnalyzed && segEnd > segStart)
                    {
                        for (int s = segStart; s < segEnd; s++)
                        {
                            if (m_Segments[s].MethodIndex == m_HighlightedMethodIndex)
                            {
                                DrawFilledRect(painter, x, barTop, w, barHeight, k_HighlightedMethodOverlay);
                                break;
                            }
                        }
                    }

                    if (isHighlighted)
                        DrawFilledRect(painter, x, barTop, w, barHeight, k_HoverOverlay);
                }
            }
        }

        static Color ModulateSegmentColor(Color segColor, bool isSelected)
        {
            if (isSelected)
                return Color.Lerp(segColor, Color.white, 0.2f);
            return segColor;
        }

        static void DrawFilledRect(Painter2D painter, float x, float y, float w, float h, Color color)
        {
            painter.fillColor = color;
            painter.BeginPath();
            painter.MoveTo(new Vector2(x, y));
            painter.LineTo(new Vector2(x + w, y));
            painter.LineTo(new Vector2(x + w, y + h));
            painter.LineTo(new Vector2(x, y + h));
            painter.ClosePath();
            painter.Fill();
        }

        // ═══════════════════════════════════════════════════
        //  DRAWING — OVERLAY BARS (bucketed zoom levels)
        // ═══════════════════════════════════════════════════

        void DrawOverlayBars(Painter2D painter, Rect rect)
        {
            if (m_OverlayBars == null || m_OverlayBarCount <= 0) return;

            float areaWidth = rect.width;
            float areaHeight = rect.height;
            float barWidth = areaWidth / m_VisibleSpan;
            painter.fillColor = k_HighlightedMethodOverlay;

            for (int i = 0; i < m_OverlayBarCount; i++)
            {
                float barHeight = m_YAxisMax > 0
                    ? (float)m_OverlayBars[i].Value / m_YAxisMax * areaHeight
                    : 0f;
                if (barHeight <= 0f) continue;

                int maskIdx = m_BarOffset + i;
                if (m_AnalyzedBarMask != null)
                {
                    if (maskIdx >= m_AnalyzedBarMask.Length || !m_AnalyzedBarMask[maskIdx]) continue;
                }
                else if (m_AnalyzedStartBar >= 0 && (i < m_AnalyzedStartBar || i > m_AnalyzedEndBar))
                    continue;

                float x = (i - m_FractionalOffset) * barWidth;
                float w = Mathf.Max(barWidth, 1f);

                float overlayBottom = m_YAxisMax > 0
                    ? areaHeight + (float)m_YPanOffset / m_YAxisMax * areaHeight
                    : areaHeight;
                float overlayTop = overlayBottom - barHeight;
                DrawFilledRect(painter, x, overlayTop, w, barHeight, k_HighlightedMethodOverlay);
            }
        }

        // ═══════════════════════════════════════════════════
        //  HIT TESTING
        // ═══════════════════════════════════════════════════

        int HitTestBar(Vector2 localPos)
        {
            if (m_Bars == null || m_BarCount <= 0) return -1;

            float w = contentRect.width;
            if (w < 1f) return -1;

            float barWidth = w / m_VisibleSpan;
            if (barWidth < 0.001f) return -1;

            int index = (int)(localPos.x / barWidth + m_FractionalOffset);
            if (index < 0 || index >= m_BarCount) return -1;
            return index;
        }

        // ═══════════════════════════════════════════════════
        //  POINTER EVENT HANDLERS
        // ═══════════════════════════════════════════════════

        void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;

            m_PointerDown = true;
            m_Dragging = false;
            m_PointerDownPos = evt.localPosition;
            m_DragStartX = evt.localPosition.x;
            m_DragCurrentX = evt.localPosition.x;

            this.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            if (!m_PointerDown) return;

            Vector2 pos = evt.localPosition;
            float dx = Mathf.Abs(pos.x - m_PointerDownPos.x);

            if (!m_Dragging)
            {
                if (dx < k_MinDragThreshold) return;
                m_Dragging = true;
            }

            m_DragCurrentX = pos.x;
            MarkDirtyRepaint();

            // Notify controller with bar indices for live updates (tooltip, etc.)
            int startBar = HitTestBar(new Vector2(m_DragStartX, 0f));
            int endBar = HitTestBar(pos);
            if (endBar < 0 && m_Bars != null && m_BarCount > 0)
                endBar = pos.x <= 0 ? 0 : m_BarCount - 1;
            if (startBar < 0 && m_Bars != null && m_BarCount > 0)
                startBar = m_DragStartX <= 0 ? 0 : m_BarCount - 1;

            if (startBar >= 0 && endBar >= 0)
            {
                int start = startBar < endBar ? startBar : endBar;
                int end   = startBar < endBar ? endBar   : startBar;

                if (SelectionChanged != null)
                    SelectionChanged(start, end);
            }

            evt.StopPropagation();
        }

        void OnPointerUp(PointerUpEvent evt)
        {
            if (evt.button != 0) return;
            if (!m_PointerDown) return;

            this.ReleasePointer(evt.pointerId);

            bool wasDragging = m_Dragging;
            m_PointerDown = false;
            m_Dragging = false;

            if (wasDragging)
            {
                // Convert pixel positions to bar indices
                int startBar = HitTestBar(new Vector2(m_DragStartX, 0f));
                int endBar = HitTestBar(new Vector2(m_DragCurrentX, 0f));
                if (startBar < 0 && m_Bars != null && m_BarCount > 0)
                    startBar = m_DragStartX <= 0 ? 0 : m_BarCount - 1;
                if (endBar < 0 && m_Bars != null && m_BarCount > 0)
                    endBar = m_DragCurrentX <= 0 ? 0 : m_BarCount - 1;

                if (startBar >= 0 && endBar >= 0)
                {
                    int start = startBar < endBar ? startBar : endBar;
                    int end   = startBar < endBar ? endBar   : startBar;

                    // Snap selection to bar boundaries now that drag is finalized
                    m_SelectionStart = start;
                    m_SelectionEnd = end;
                    MarkDirtyRepaint();

                    if (DragCompleted != null)
                        DragCompleted(start, end);
                }
            }
            else
            {
                // Click (no drag)
                int bar = HitTestBar(evt.localPosition);
                if (bar >= 0)
                {
                    m_HighlightedBar = bar;
                    MarkDirtyRepaint();

                    if (BarClicked != null)
                        BarClicked(bar, evt.localPosition.y);
                }
            }

            evt.StopPropagation();
        }

        void OnPointerLeave(PointerLeaveEvent evt)
        {
            // Don't cancel drag when pointer is captured — the user may drag
            // outside the element and come back. Capture ensures we keep
            // receiving PointerMove/PointerUp events.
            if (m_PointerDown && this.HasPointerCapture(evt.pointerId))
                return;

            m_PointerDown = false;
            m_Dragging = false;
        }

        // ═══════════════════════════════════════════════════
        //  KEYBOARD NAVIGATION
        // ═══════════════════════════════════════════════════

        void OnKeyDown(KeyDownEvent evt)
        {
            if (m_BarCount <= 0) return;

            int step = evt.shiftKey ? 10 : 1;
            bool handled = false;

            switch (evt.keyCode)
            {
                case KeyCode.LeftArrow:
                    MoveHighlightedBar(-step);
                    handled = true;
                    break;

                case KeyCode.RightArrow:
                    MoveHighlightedBar(step);
                    handled = true;
                    break;

            }

            if (handled)
            {
                MarkDirtyRepaint();
                if (BarClicked != null && m_HighlightedBar >= 0)
                    BarClicked(m_HighlightedBar, -1f);
                evt.StopPropagation();
            }
        }

        void MoveHighlightedBar(int delta)
        {
            if (m_HighlightedBar < 0)
            {
                // No bar highlighted — start from center of analyzed range, or center of visible bars
                if (m_AnalyzedBarMask != null)
                {
                    // Find first and last analyzed bar from mask
                    int first = -1, last = -1;
                    for (int i = 0; i < m_BarCount; i++)
                    {
                        int maskIdx = m_BarOffset + i;
                        if (maskIdx < m_AnalyzedBarMask.Length && m_AnalyzedBarMask[maskIdx])
                        {
                            if (first < 0) first = i;
                            last = i;
                        }
                    }
                    m_HighlightedBar = first >= 0 ? (first + last) / 2 : m_BarCount / 2;
                }
                else if (m_AnalyzedStartBar >= 0 && m_AnalyzedEndBar >= 0)
                    m_HighlightedBar = (m_AnalyzedStartBar + m_AnalyzedEndBar) / 2;
                else
                    m_HighlightedBar = m_BarCount / 2;
                return;
            }

            int newBar = m_HighlightedBar + delta;
            if (newBar < 0) newBar = 0;
            if (newBar >= m_BarCount) newBar = m_BarCount - 1;
            m_HighlightedBar = newBar;
        }

    }
}
