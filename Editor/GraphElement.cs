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
        public float X;          // left edge in pixels
        public float W;          // width in pixels
        public float Height;     // height in pixels (0 to maxHeight)
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
        static readonly Color k_BarSelected       = new Color(0.4f, 0.8f, 0.73f);
        static readonly Color k_BarHighlighted    = new Color(0.9f, 0.75f, 0.3f);
        static readonly Color k_SelectionBg       = new Color(0.25f, 0.35f, 0.55f, 0.3f);
        static readonly Color k_OverlayColor      = new Color(1f, 1f, 1f, 0.85f);
        static readonly Color k_BarDimmed         = new Color(0.27f, 0.67f, 0.6f, 0.3f);

        // ═══════════════════════════════════════════════════
        //  BAR DATA
        // ═══════════════════════════════════════════════════

        BarData[] m_Bars;
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

        int m_AnalyzedStartBar = -1;
        int m_AnalyzedEndBar   = -1;
        bool[] m_AnalyzedBarMask; // per-bar mask for non-contiguous analyzed ranges

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

        public event Action<int> BarClicked;
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

        public void SetBarData(BarData[] bars, int count)
        {
            m_Bars = bars;
            m_BarCount = bars != null ? count : 0;
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

        public void SetHighlightedBar(int barIndex)
        {
            if (m_HighlightedBar == barIndex) return;
            m_HighlightedBar = barIndex;
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

            float left  = m_Bars[sStart].X;
            float right = m_Bars[sEnd].X + m_Bars[sEnd].W;

            painter.fillColor = k_SelectionBg;
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

            float areaHeight = rect.height;

            for (int i = 0; i < m_BarCount; i++)
            {
                // Scale bar height to actual content rect (was computed against k_GraphHeight)
                float barHeight = m_YAxisMax > 0
                    ? (float)m_Bars[i].Value / m_YAxisMax * areaHeight
                    : 0f;
                if (barHeight <= 0f) continue;

                // Determine bar color — dimming for bars outside analyzed range.
                // Prefer per-bar mask (supports non-contiguous analyzed bars in sorted mode).
                // Fallback: m_AnalyzedStartBar == -1 means no analysis (no dimming),
                //           m_AnalyzedStartBar == -2 means analysis off-screen (dim all).
                bool inAnalyzed;
                if (m_AnalyzedBarMask != null)
                    inAnalyzed = i < m_AnalyzedBarMask.Length && m_AnalyzedBarMask[i];
                else
                    inAnalyzed = m_AnalyzedStartBar == -1
                        || (m_AnalyzedStartBar >= 0 && i >= m_AnalyzedStartBar && i <= m_AnalyzedEndBar);
                Color color;
                if (i == m_HighlightedBar)
                    color = k_BarHighlighted;
                else if (sStart >= 0 && i >= sStart && i <= sEnd)
                    color = k_BarSelected;
                else if (!inAnalyzed)
                    color = k_BarDimmed;
                else
                    color = k_BarNormal;

                float x = m_Bars[i].X;
                float w = m_Bars[i].W;
                float top = areaHeight - barHeight;

                painter.fillColor = color;
                painter.BeginPath();
                painter.MoveTo(new Vector2(x, top));
                painter.LineTo(new Vector2(x + w, top));
                painter.LineTo(new Vector2(x + w, areaHeight));
                painter.LineTo(new Vector2(x, areaHeight));
                painter.ClosePath();
                painter.Fill();
            }
        }

        // ═══════════════════════════════════════════════════
        //  DRAWING — OVERLAY BARS
        // ═══════════════════════════════════════════════════

        void DrawOverlayBars(Painter2D painter, Rect rect)
        {
            if (m_OverlayBars == null || m_OverlayBarCount <= 0) return;

            float areaHeight = rect.height;
            painter.fillColor = k_OverlayColor;

            for (int i = 0; i < m_OverlayBarCount; i++)
            {
                // Scale overlay bar height to actual content rect
                float barHeight = m_YAxisMax > 0
                    ? (float)m_OverlayBars[i].Value / m_YAxisMax * areaHeight
                    : 0f;
                if (barHeight <= 0f) continue;

                // Skip overlay for bars outside analyzed range
                if (m_AnalyzedBarMask != null)
                {
                    if (i >= m_AnalyzedBarMask.Length || !m_AnalyzedBarMask[i]) continue;
                }
                else if (m_AnalyzedStartBar >= 0 && (i < m_AnalyzedStartBar || i > m_AnalyzedEndBar))
                    continue;

                float x = m_OverlayBars[i].X;
                float w = m_OverlayBars[i].W;
                float top = areaHeight - barHeight;

                painter.BeginPath();
                painter.MoveTo(new Vector2(x, top));
                painter.LineTo(new Vector2(x + w, top));
                painter.LineTo(new Vector2(x + w, areaHeight));
                painter.LineTo(new Vector2(x, areaHeight));
                painter.ClosePath();
                painter.Fill();
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
                    if (pos.x <= m_Bars[0].X)
                        currentBar = 0;
                    else if (pos.x >= m_Bars[m_BarCount - 1].X + m_Bars[m_BarCount - 1].W)
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
                        BarClicked(bar);
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
                    BarClicked(m_HighlightedBar);
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
                        if (i < m_AnalyzedBarMask.Length && m_AnalyzedBarMask[i])
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
