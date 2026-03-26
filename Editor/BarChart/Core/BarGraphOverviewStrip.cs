using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using GCAllocBreakdown.BarChart.Events;

namespace GCAllocBreakdown.BarChart.Core
{
    /// <summary>
    /// Overview strip that shows the full dataset at zoom-1 with a viewport
    /// indicator rectangle reflecting the bound chart's current zoom/pan position.
    /// Drag the indicator to pan; drag its left/right edges to resize the viewport.
    ///
    /// Usage
    /// ─────
    ///  1. Place above (or below) a <see cref="BarGraphElement"/> in a vertical layout.
    ///  2. Call <see cref="BindTo"/> to wire automatic viewport tracking.
    ///  3. Call the same <see cref="SetData"/> overload you use on the main chart.
    ///
    /// <code>
    ///   var overview = new BarGraphOverviewStrip();
    ///   overview.BindTo(mainChart);
    ///   container.Add(overview);
    ///   container.Add(mainChart);
    ///
    ///   mainChart.SetData(values, color);
    ///   overview.SetData(values, color);
    /// </code>
    /// </summary>
    public sealed class BarGraphOverviewStrip : VisualElement
    {
        // ── USS class names ──────────────────────────────────────────────────
        public const string UssClassName        = "bar-graph-overview-strip";
        public const string InnerChartClassName = "bar-graph--overview";
        public const string IndicatorClassName  = "bar-graph-overview-strip__indicator";
        public const string HandleClassName     = "bar-graph-overview-strip__handle";
        public const string ResizingClassName   = "bar-graph-overview-strip__indicator--resizing";

        // ── Custom style properties ──────────────────────────────────────────
        static readonly CustomStyleProperty<Color> s_indicatorFillColor
            = new("--overview-indicator-fill-color");
        static readonly CustomStyleProperty<Color> s_indicatorBorderColor
            = new("--overview-indicator-border-color");
        static readonly CustomStyleProperty<float> s_indicatorBorderWidth
            = new("--overview-indicator-border-width");

        // ── Resolved indicator visuals (cached) ──────────────────────────────
        private Color _indicatorFill        = new Color(1f, 1f, 1f, 0.12f);
        private Color _indicatorBorder      = new Color(0.4f, 0.7f, 1f, 0.7f);
        private float _indicatorBorderWidth = 1.5f;

        // ── Children ─────────────────────────────────────────────────────────
        private readonly BarGraphElement _innerChart;
        private readonly VisualElement   _indicator;
        private readonly VisualElement   _handleLeft;
        private readonly VisualElement   _handleRight;

        // ── Bound source ─────────────────────────────────────────────────────
        private BarGraphElement _source;

        // ── Shared default stylesheet ────────────────────────────────────────
        private static StyleSheet s_defaultSheet;

        // ── Drag state ───────────────────────────────────────────────────────
        private enum DragMode { None, Pan, ResizeLeft, ResizeRight }

        const float EdgeGrabWidth = 5f;

        private DragMode _dragMode;
        private float    _dragStartPointerX;
        private float    _dragStartPanX;
        private float    _dragStartZoomX;
        private float    _dragStartIndicatorL; // indicator left at drag start (px)
        private float    _dragStartIndicatorW; // indicator width at drag start (px)
        private int      _pointerId = -1;

        // ─────────────────────────────────────────────────────────────────────
        //  Construction
        // ─────────────────────────────────────────────────────────────────────

        public BarGraphOverviewStrip()
        {
            AddToClassList(UssClassName);

            if (s_defaultSheet == null)
                s_defaultSheet = Resources.Load<StyleSheet>("BarGraphOverviewStrip");
            if (s_defaultSheet != null)
                styleSheets.Add(s_defaultSheet);

            style.overflow = Overflow.Hidden;
            style.flexShrink = 0;
            pickingMode = PickingMode.Position;

            // ── Inner chart: full dataset, no chrome ─────────────────────────
            _innerChart = new BarGraphElement();
            _innerChart.AddToClassList(InnerChartClassName);
            _innerChart.style.position = Position.Absolute;
            _innerChart.style.left   = 0;
            _innerChart.style.top    = 0;
            _innerChart.style.right  = 0;
            _innerChart.style.bottom = 0;
            _innerChart.pickingMode  = PickingMode.Ignore;
            _innerChart.focusable    = false;
            // No SetInputSource, no AddHandler → purely visual
            _innerChart.UpdateSettings(new BarGraphSettings
            {
                ShowGrid         = false,
                ShowAxes         = false,
                EnableMouseZoomX = false,
                EnableMouseZoomY = false,
                EnableMousePan   = false,
                EnableSelection  = false,
                MaxXLabels       = 0,
                MaxYLabels       = 0,
            });
            Add(_innerChart);

            // ── Viewport indicator ───────────────────────────────────────────
            _indicator = new VisualElement();
            _indicator.AddToClassList(IndicatorClassName);
            _indicator.style.position = Position.Absolute;
            _indicator.style.top    = 0;
            _indicator.style.bottom = 0;
            _indicator.pickingMode  = PickingMode.Position;
            _indicator.style.display = DisplayStyle.None;
            ApplyIndicatorStyle();

            // Edge handles
            _handleLeft = MakeHandle();
            _handleLeft.style.left = 0;
            _indicator.Add(_handleLeft);

            _handleRight = MakeHandle();
            _handleRight.style.right = 0;
            _indicator.Add(_handleRight);

            Add(_indicator);

            // ── Events ───────────────────────────────────────────────────────
            RegisterCallback<CustomStyleResolvedEvent>(_ => ResolveCustomStyles());
            RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (_source != null)
                    SyncIndicator(_source.ViewState.ZoomX, _source.ViewState.PanX);
            });

            _indicator.RegisterCallback<PointerDownEvent>(OnPointerDown);
            _handleLeft.RegisterCallback<PointerDownEvent>(OnPointerDown);
            _handleRight.RegisterCallback<PointerDownEvent>(OnPointerDown);
            _indicator.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _indicator.RegisterCallback<PointerUpEvent>(OnPointerUp);
            _indicator.RegisterCallback<PointerCaptureOutEvent>(_ => EndDrag());
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public API – binding
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Binds this overview strip to a main chart. Subscribes to
        /// <see cref="BarGraphElement.ViewChanged"/> for automatic viewport tracking.
        /// Call <see cref="Unbind"/> or bind to a different source to release.
        /// </summary>
        public void BindTo(BarGraphElement source)
        {
            Unbind();
            _source = source;
            if (_source == null) return;

            _source.ViewChanged += OnSourceViewChanged;
            SyncIndicator(_source.ViewState.ZoomX, _source.ViewState.PanX);
        }

        /// <summary>Detach from the currently bound chart.</summary>
        public void Unbind()
        {
            if (_source != null)
            {
                _source.ViewChanged -= OnSourceViewChanged;
                _source = null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public API – data  (forward to inner chart)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Full stacked-bar data.</summary>
        public void SetData(BarEntry[] bars, int barCount, BarSegment[] segments, int segCount)
            => _innerChart.SetData(bars, barCount, segments, segCount);

        /// <summary>Convenience: flat float values, single colour per bar.</summary>
        public void SetData(IList<float> values, Color? barColor = null)
            => _innerChart.SetData(values, barColor);

        /// <summary>Convenience: single-segment BarEntry list.</summary>
        public void SetData(IList<BarEntry> bars)
            => _innerChart.SetData(bars);

        /// <summary>Remove all bars and segments.</summary>
        public void ClearData() => _innerChart.ClearData();

        /// <summary>Per-bar visual override forwarded to the inner chart.</summary>
        public Func<int, BarVisualOverride?> BarVisualProvider
        {
            get => _innerChart.BarVisualProvider;
            set => _innerChart.BarVisualProvider = value;
        }



        // ─────────────────────────────────────────────────────────────────────
        //  Viewport indicator sync
        // ─────────────────────────────────────────────────────────────────────

        private void OnSourceViewChanged(ViewChangedEventArgs args)
        {
            _innerChart.SetSortMode(args.SortMode, args.SortDescending);
            SyncIndicator(args.ZoomX, args.PanX);
        }

        private void SyncIndicator(float zoomX, float panX)
        {
            bool visible = zoomX > 1f + float.Epsilon;
            _indicator.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible) return;

            // Align to the inner chart's plot area, not the full strip width
            float padL  = _innerChart.VisPaddingLeft;
            float padR  = _innerChart.VisPaddingRight;
            float plotW = _innerChart.contentRect.width - padL - padR;
            if (plotW < 1f) return;

            int totalBars = _innerChart.BarCount;
            if (totalBars <= 0) return;

            // Same math as BarGraphScrollbarHandler.SyncX
            float visibleFraction = Mathf.Clamp01(1f / zoomX);
            float indicatorW     = plotW * visibleFraction;
            float scrollablePx   = plotW - indicatorW;
            float maxPanX        = totalBars * (1f - 1f / zoomX);
            float indicatorL     = padL + (maxPanX > 0f ? scrollablePx * (panX / maxPanX) : 0f);

            _indicator.style.width = indicatorW;
            _indicator.style.left  = indicatorL;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Drag interaction
        // ─────────────────────────────────────────────────────────────────────

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (_source == null || evt.button != 0) return;

            evt.StopPropagation();
            _pointerId = evt.pointerId;
            _indicator.CapturePointer(_pointerId);

            _dragStartPointerX  = evt.position.x;
            _dragStartPanX      = _source.ViewState.PanX;
            _dragStartZoomX     = _source.ViewState.ZoomX;
            _dragStartIndicatorL = _indicator.resolvedStyle.left;
            _dragStartIndicatorW = _indicator.resolvedStyle.width;

            // Determine drag mode from pointer position relative to indicator bounds
            float indicLeft = _indicator.worldBound.x;
            float indicW    = _indicator.worldBound.width;
            float pointerX  = evt.position.x;

            if (pointerX <= indicLeft + EdgeGrabWidth)
                _dragMode = DragMode.ResizeLeft;
            else if (pointerX >= indicLeft + indicW - EdgeGrabWidth)
                _dragMode = DragMode.ResizeRight;
            else
                _dragMode = DragMode.Pan;

            if (_dragMode == DragMode.ResizeLeft || _dragMode == DragMode.ResizeRight)
                _indicator.AddToClassList(ResizingClassName);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_dragMode == DragMode.None || _source == null) return;
            if (!_indicator.HasPointerCapture(_pointerId)) return;

            float dx   = evt.position.x - _dragStartPointerX;
            float padL = _innerChart.VisPaddingLeft;
            float padR = _innerChart.VisPaddingRight;
            float plotW = _innerChart.contentRect.width - padL - padR;
            if (plotW < 1f) return;

            int totalBars = _innerChart.BarCount;
            if (totalBars <= 0) return;

            float curZoomY = _source.ViewState.ZoomY;
            float curPanY  = _source.ViewState.PanY;

            switch (_dragMode)
            {
                case DragMode.Pan:
                {
                    // Convert pixel delta to pan delta
                    float panDelta = dx / plotW * totalBars;
                    _source.InternalSetPan(_dragStartPanX + panDelta, curPanY);
                    break;
                }

                case DragMode.ResizeLeft:
                {
                    // Left edge moves by dx; right edge stays fixed
                    float newL = Mathf.Clamp(_dragStartIndicatorL + dx, padL,
                        _dragStartIndicatorL + _dragStartIndicatorW - EdgeGrabWidth * 2f);
                    float newW = (_dragStartIndicatorL + _dragStartIndicatorW) - newL;
                    float newZoomX = plotW / Mathf.Max(1f, newW);
                    float maxPanX  = totalBars * (1f - 1f / newZoomX);
                    float scrollable = plotW - newW;
                    float newPanX  = scrollable > 0f ? (newL - padL) / scrollable * maxPanX : 0f;
                    _source.InternalSetZoom(newZoomX, curZoomY, newPanX, curPanY);
                    break;
                }

                case DragMode.ResizeRight:
                {
                    // Right edge moves by dx; left edge stays fixed
                    float newW = Mathf.Clamp(_dragStartIndicatorW + dx, EdgeGrabWidth * 2f, plotW);
                    float newZoomX = plotW / Mathf.Max(1f, newW);
                    float maxPanX  = totalBars * (1f - 1f / newZoomX);
                    float scrollable = plotW - newW;
                    float newPanX  = scrollable > 0f
                        ? (_dragStartIndicatorL - padL) / scrollable * maxPanX : 0f;
                    _source.InternalSetZoom(newZoomX, curZoomY, newPanX, curPanY);
                    break;
                }
            }
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (_dragMode == DragMode.None) return;
            if (evt.pointerId != _pointerId) return;
            EndDrag();
        }

        private void EndDrag()
        {
            if (_pointerId >= 0 && _indicator.HasPointerCapture(_pointerId))
                _indicator.ReleasePointer(_pointerId);
            _dragMode  = DragMode.None;
            _pointerId = -1;
            _indicator.RemoveFromClassList(ResizingClassName);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Handle factory
        // ─────────────────────────────────────────────────────────────────────

        private static VisualElement MakeHandle()
        {
            var h = new VisualElement();
            h.AddToClassList(HandleClassName);
            h.style.position = Position.Absolute;
            h.style.top    = 0;
            h.style.bottom = 0;
            h.pickingMode  = PickingMode.Position;
            return h;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  USS custom style resolution
        // ─────────────────────────────────────────────────────────────────────

        private void ResolveCustomStyles()
        {
            var cs = customStyle;
            if (cs.TryGetValue(s_indicatorFillColor,   out var fill))   _indicatorFill = fill;
            if (cs.TryGetValue(s_indicatorBorderColor, out var border)) _indicatorBorder = border;
            if (cs.TryGetValue(s_indicatorBorderWidth, out var width))  _indicatorBorderWidth = width;
            ApplyIndicatorStyle();
        }

        private void ApplyIndicatorStyle()
        {
            _indicator.style.backgroundColor = _indicatorFill;

            _indicator.style.borderLeftColor   = _indicatorBorder;
            _indicator.style.borderRightColor  = _indicatorBorder;
            _indicator.style.borderTopColor    = _indicatorBorder;
            _indicator.style.borderBottomColor = _indicatorBorder;

            _indicator.style.borderLeftWidth   = _indicatorBorderWidth;
            _indicator.style.borderRightWidth  = _indicatorBorderWidth;
            _indicator.style.borderTopWidth    = _indicatorBorderWidth;
            _indicator.style.borderBottomWidth = _indicatorBorderWidth;
        }
    }
}
