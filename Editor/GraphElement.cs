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

        const float k_DragThreshold = 3f;

        // ═══════════════════════════════════════════════════
        //  COLORS
        // ═══════════════════════════════════════════════════

        static readonly Color k_Background       = new Color(0.18f, 0.18f, 0.18f);
        static readonly Color k_GridLineColor     = new Color(0.3f, 0.3f, 0.3f, 0.6f);
        static readonly Color k_BarNormal         = new Color(0.27f, 0.67f, 0.6f);
        static readonly Color k_SelectionAreaBarColor      = new Color(0.4f, 0.8f, 0.73f);
        static readonly Color k_SelectionAreaBgColor = new Color(0.25f, 0.35f, 0.55f, 0.3f);
        static readonly Color k_BarSelectedOverlayColor  = new Color(1f, 1f, 1f, 0.7f);
        static readonly Color k_BarDimmed         = new Color(0.27f, 0.67f, 0.6f, 0.3f);
        static readonly Color k_HoverOverlay      = new Color(1f, 1f, 1f, 0.12f);

        // ═══════════════════════════════════════════════════
        //  BAR DATA
        // ═══════════════════════════════════════════════════

        BarData[] m_Bars;
        int m_BarOffset;
        int m_BarCount;

        BarData[] m_OverlayBars;
        int m_OverlayBarCount;

        GridLine[] m_GridLines;
        int m_GridLineCount;

        long m_YAxisMax;
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
        int m_OverlayMethodIndex = -1; // method index for segment-aligned overlay (-1 = bottom-aligned)

        // ═══════════════════════════════════════════════════
        //  DRAG STATE
        // ═══════════════════════════════════════════════════

        bool m_PointerDown;
        bool m_Dragging;
        Vector2 m_PointerDownPos;
        int m_DragStartBar = -1;
        int m_DragCurrentBar = -1;

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

        public void SetBarData(BarData[] bars, int offset, int count)
        {
            m_Bars = bars;
            m_BarOffset = offset;
            m_BarCount = bars != null ? count : 0;
            MarkDirtyRepaint();
        }

        public void SetOverlayData(BarData[] overlayBars, int count, int methodIndex = -1)
        {
            m_OverlayBars = overlayBars;
            m_OverlayBarCount = overlayBars != null ? count : 0;
            m_OverlayMethodIndex = methodIndex;
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
                    ? (float)m_GridLines[i].Value / m_YAxisMax * rect.height
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
            if (m_SelectionStart < 0 || m_SelectionEnd < 0) return;
            if (m_Bars == null || m_BarCount <= 0) return;

            int sStart = m_SelectionStart < m_SelectionEnd ? m_SelectionStart : m_SelectionEnd;
            int sEnd   = m_SelectionStart < m_SelectionEnd ? m_SelectionEnd   : m_SelectionStart;

            if (sStart >= m_BarCount) return;
            if (sEnd >= m_BarCount) sEnd = m_BarCount - 1;

            float barWidth = rect.width / m_BarCount;
            float left  = sStart * barWidth;
            float right = (sEnd + 1) * barWidth;

            painter.fillColor = k_SelectionAreaBgColor;
            painter.BeginPath();
            painter.MoveTo(new Vector2(left, 0f));
            painter.LineTo(new Vector2(right, 0f));
            painter.LineTo(new Vector2(right, rect.height));
            painter.LineTo(new Vector2(left, rect.height));
            painter.ClosePath();
            painter.Fill();
        }

        // ═══════════════════════════════════════════════════
        //  DRAWING — BARS
        // ═══════════════════════════════════════════════════

        void DrawBars(Painter2D painter, Rect rect)
        {
            if (m_Bars == null || m_BarCount <= 0) return;

            // Normalize selection range
            int sStart = -1, sEnd = -1;
            if (m_SelectionStart >= 0 && m_SelectionEnd >= 0)
            {
                sStart = m_SelectionStart < m_SelectionEnd ? m_SelectionStart : m_SelectionEnd;
                sEnd   = m_SelectionStart < m_SelectionEnd ? m_SelectionEnd   : m_SelectionStart;
            }

            float areaWidth = rect.width;
            float areaHeight = rect.height;
            float barWidth = areaWidth / m_BarCount;

            for (int i = 0; i < m_BarCount; i++)
            {
                int srcIdx = m_BarOffset + i;

                // Scale bar height to actual content rect
                float barHeight = m_YAxisMax > 0
                    ? (float)m_Bars[srcIdx].Value / m_YAxisMax * areaHeight
                    : 0f;
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

                float x = i * barWidth;
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

                    // Only use stacked rendering when 2+ named segments exist;
                    // otherwise stacking adds no visual information.
                    int namedCount = 0;
                    for (int s = segStart; s < segEnd && namedCount < 2; s++)
                    {
                        if (m_Segments[s].MethodIndex != MethodColorPalette.k_OthersIndex)
                            namedCount++;
                    }
                    useStacked = namedCount >= 2;
                }

                if (useStacked)
                {
                    // STACKED: multiple methods — draw colored segments
                    float currentY = areaHeight;
                    for (int s = segStart; s < segEnd; s++)
                    {
                        float segH = m_YAxisMax > 0
                            ? (float)m_Segments[s].Bytes / m_YAxisMax * areaHeight
                            : 0f;
                        if (segH < 0.5f) continue;

                        Color segColor = m_MethodPalette.GetColor(m_Segments[s].MethodIndex);
                        segColor = ModulateSegmentColor(segColor, isSelected);

                        float segTop = currentY - segH;

                        DrawFilledRect(painter, x, segTop, w, segH, segColor);

                        if (isHighlighted && s == m_HighlightedSegment)
                            DrawFilledRect(painter, x, segTop, w, segH, k_HoverOverlay);

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

                    DrawFilledRect(painter, x, areaHeight - barHeight, w, barHeight, color);

                    if (isHighlighted)
                        DrawFilledRect(painter, x, areaHeight - barHeight, w, barHeight, k_HoverOverlay);
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
        //  DRAWING — OVERLAY BARS
        // ═══════════════════════════════════════════════════

        void DrawOverlayBars(Painter2D painter, Rect rect)
        {
            if (m_OverlayBars == null || m_OverlayBarCount <= 0) return;

            float areaWidth = rect.width;
            float areaHeight = rect.height;
            float barWidth = areaWidth / m_BarCount;
            painter.fillColor = k_BarSelectedOverlayColor;

            for (int i = 0; i < m_OverlayBarCount; i++)
            {
                // Scale overlay bar height to actual content rect
                float barHeight = m_YAxisMax > 0
                    ? (float)m_OverlayBars[i].Value / m_YAxisMax * areaHeight
                    : 0f;
                if (barHeight <= 0f) continue;

                // Skip overlay for bars outside analyzed range
                int maskIdx = m_BarOffset + i;
                if (m_AnalyzedBarMask != null)
                {
                    if (maskIdx >= m_AnalyzedBarMask.Length || !m_AnalyzedBarMask[maskIdx]) continue;
                }
                else if (m_AnalyzedStartBar >= 0 && (i < m_AnalyzedStartBar || i > m_AnalyzedEndBar))
                    continue;

                float x = i * barWidth;
                float w = Mathf.Max(barWidth, 1f);

                // When a method index is set, draw overlay at the matching segment's
                // Y position instead of from the bottom of the chart.
                if (m_OverlayMethodIndex >= 0 && m_HasSegmentData && m_SegmentOffsets != null)
                {
                    int srcIdx = m_BarOffset + i;
                    int segLookup = m_SegmentIndexMap != null ? m_SegmentIndexMap[srcIdx] : srcIdx;
                    if (segLookup + 1 < m_SegmentOffsets.Length)
                    {
                        int segStart = m_SegmentOffsets[segLookup];
                        int segEnd = m_SegmentOffsets[segLookup + 1];

                        float currentY = areaHeight;
                        for (int s = segStart; s < segEnd; s++)
                        {
                            float segH = m_YAxisMax > 0
                                ? (float)m_Segments[s].Bytes / m_YAxisMax * areaHeight
                                : 0f;
                            float segTop = currentY - segH;

                            if (m_Segments[s].MethodIndex == m_OverlayMethodIndex)
                            {
                                // Draw overlay at this segment's position, clamped to segment bounds
                                float overlayTop = currentY - barHeight;
                                if (overlayTop < segTop) overlayTop = segTop;
                                DrawFilledRect(painter, x, overlayTop, w, currentY - overlayTop,
                                    k_BarSelectedOverlayColor);
                                break;
                            }
                            currentY = segTop;
                        }
                        continue;
                    }
                }

                // Default: draw overlay from the bottom of the chart
                float top = areaHeight - barHeight;
                DrawFilledRect(painter, x, top, w, barHeight, k_BarSelectedOverlayColor);
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

            // Simple division — works regardless of sort order since bars
            // are uniform width and the controller uses display-position indices.
            float barWidth = w / m_BarCount;
            if (barWidth < 0.001f) return -1;

            int index = (int)(localPos.x / barWidth);
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
            m_DragStartBar = HitTestBar(evt.localPosition);
            m_DragCurrentBar = m_DragStartBar;

            this.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            if (!m_PointerDown) return;

            Vector2 pos = evt.localPosition;
            float dx = pos.x - m_PointerDownPos.x;
            float dy = pos.y - m_PointerDownPos.y;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);

            if (!m_Dragging)
            {
                if (dist < k_DragThreshold) return;
                m_Dragging = true;
            }

            int currentBar = HitTestBar(pos);
            if (currentBar < 0)
            {
                // Clamp to nearest edge
                if (m_Bars != null && m_BarCount > 0)
                {
                    if (pos.x <= 0)
                        currentBar = 0;
                    else if (pos.x >= contentRect.width)
                        currentBar = m_BarCount - 1;
                }
            }

            if (currentBar >= 0 && currentBar != m_DragCurrentBar)
            {
                m_DragCurrentBar = currentBar;

                int start = m_DragStartBar < m_DragCurrentBar ? m_DragStartBar : m_DragCurrentBar;
                int end   = m_DragStartBar < m_DragCurrentBar ? m_DragCurrentBar : m_DragStartBar;

                m_SelectionStart = start;
                m_SelectionEnd = end;

                MarkDirtyRepaint();

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
                // Finalize drag selection
                if (m_DragStartBar >= 0 && m_DragCurrentBar >= 0)
                {
                    int start = m_DragStartBar < m_DragCurrentBar ? m_DragStartBar : m_DragCurrentBar;
                    int end   = m_DragStartBar < m_DragCurrentBar ? m_DragCurrentBar : m_DragStartBar;

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

            m_DragStartBar = -1;
            m_DragCurrentBar = -1;

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
            m_DragStartBar = -1;
            m_DragCurrentBar = -1;
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
