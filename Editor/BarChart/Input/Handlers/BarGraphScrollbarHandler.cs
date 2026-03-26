using UnityEngine;
using UnityEngine.UIElements;
using GCAllocBreakdown.BarChart.Core;
using GCAllocBreakdown.BarChart.Events;

namespace GCAllocBreakdown.BarChart.Input.Handlers
{
    /// <summary>
    /// Adds horizontal and vertical scrollbar overlays to the chart that reflect
    /// the current zoom/pan state. Each scrollbar is only visible when its axis
    /// is zoomed in (zoom &gt; 1). The thumb is sized proportionally to the
    /// visible fraction — a larger thumb means less zoom, smaller means more.
    /// Dragging the thumb drives <see cref="BarGraphElement.InternalSetPan"/>.
    /// </summary>
    public sealed class BarGraphScrollbarHandler : IBarGraphHandler
    {
        private BarGraphElement _element;

        private VisualElement _trackX, _thumbX;
        private VisualElement _trackY, _thumbY;

        private bool _syncing;
        private bool _draggingX, _draggingY;
        private float _dragStartPan;
        private float _dragStartPointer;

        // ── Theme ───────────────────────────────────────────────────────────

        private static readonly Color TrackColor      = new Color(1f, 1f, 1f, 0.04f);
        private static readonly Color ThumbColor      = new Color(1f, 1f, 1f, 0.18f);
        private static readonly Color ThumbHoverColor = new Color(1f, 1f, 1f, 0.32f);
        private static readonly Color ThumbDragColor  = new Color(1f, 1f, 1f, 0.40f);

        private const float BarThickness = 8f;
        private const float CornerRadius = 4f;
        private const float MinThumbPx   = 20f;

        // ── IBarGraphHandler ────────────────────────────────────────────────

        public void Register(BarGraphEventBus actions, BarGraphElement element)
        {
            _element = element;

            BuildTrack(out _trackX, out _thumbX, isHorizontal: true);
            BuildTrack(out _trackY, out _thumbY, isHorizontal: false);

            ApplyLayout();

            _element.Add(_trackX);
            _element.Add(_trackY);

            // Drag on thumb
            _thumbX.RegisterCallback<PointerDownEvent>(OnThumbXDown);
            _thumbX.RegisterCallback<PointerMoveEvent>(OnThumbXMove);
            _thumbX.RegisterCallback<PointerUpEvent>(OnThumbXUp);

            _thumbY.RegisterCallback<PointerDownEvent>(OnThumbYDown);
            _thumbY.RegisterCallback<PointerMoveEvent>(OnThumbYMove);
            _thumbY.RegisterCallback<PointerUpEvent>(OnThumbYUp);

            // Click on track (jump to position)
            _trackX.RegisterCallback<PointerDownEvent>(OnTrackXClick);
            _trackY.RegisterCallback<PointerDownEvent>(OnTrackYClick);

            // Hover feedback
            _thumbX.RegisterCallback<PointerEnterEvent>(_ => { if (!_draggingX) _thumbX.style.backgroundColor = ThumbHoverColor; });
            _thumbX.RegisterCallback<PointerLeaveEvent>(_ => { if (!_draggingX) _thumbX.style.backgroundColor = ThumbColor; });
            _thumbY.RegisterCallback<PointerEnterEvent>(_ => { if (!_draggingY) _thumbY.style.backgroundColor = ThumbHoverColor; });
            _thumbY.RegisterCallback<PointerLeaveEvent>(_ => { if (!_draggingY) _thumbY.style.backgroundColor = ThumbColor; });

            _element.ViewChanged += OnViewChanged;
        }

        public void Unregister(BarGraphEventBus actions)
        {
            if (_element != null)
            {
                _element.ViewChanged -= OnViewChanged;
                _trackX?.RemoveFromHierarchy();
                _trackY?.RemoveFromHierarchy();
            }

            _trackX = _thumbX = null;
            _trackY = _thumbY = null;
            _element = null;
        }

        // ── Build ───────────────────────────────────────────────────────────

        private static void BuildTrack(out VisualElement track, out VisualElement thumb, bool isHorizontal)
        {
            track = new VisualElement();
            track.style.position = Position.Absolute;
            track.style.backgroundColor = TrackColor;
            track.style.display = DisplayStyle.None;
            track.pickingMode = PickingMode.Position;
            SetRadius(track, CornerRadius);

            thumb = new VisualElement();
            thumb.style.position = Position.Absolute;
            thumb.style.backgroundColor = ThumbColor;
            thumb.pickingMode = PickingMode.Position;
            SetRadius(thumb, CornerRadius);

            if (isHorizontal)
            {
                thumb.style.top    = 0;
                thumb.style.bottom = 0;
            }
            else
            {
                thumb.style.left  = 0;
                thumb.style.right = 0;
            }

            track.Add(thumb);
        }

        private void ApplyLayout()
        {
            // Centre the scrollbar in the space below X labels
            float belowLabelsX = _element.VisPaddingBottom - _element.VisXLabelOffsetY - _element.VisLabelHeight;
            float insetBottom  = Mathf.Max(0f, (belowLabelsX - BarThickness) * 0.5f);

            _trackX.style.left   = _element.VisPaddingLeft;
            _trackX.style.right  = _element.VisPaddingRight;
            _trackX.style.bottom = insetBottom;
            _trackX.style.height = BarThickness;

            // Centre the scrollbar in the right padding area
            float insetRight = Mathf.Max(0f, (_element.VisPaddingRight - BarThickness) * 0.5f);

            _trackY.style.top    = _element.VisPaddingTop;
            _trackY.style.bottom = _element.VisPaddingBottom;
            _trackY.style.right  = insetRight;
            _trackY.style.width  = BarThickness;
        }

        private static void SetRadius(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius     = r;
            el.style.borderTopRightRadius    = r;
            el.style.borderBottomLeftRadius   = r;
            el.style.borderBottomRightRadius  = r;
        }

        // ── Sync (view → scrollbar) ─────────────────────────────────────────

        private void OnViewChanged(ViewChangedEventArgs args)
        {
            _syncing = true;
            try
            {
                SyncX(args);
                SyncY(args);
            }
            finally
            {
                _syncing = false;
            }
        }

        private void SyncX(ViewChangedEventArgs args)
        {
            bool visible = args.ZoomX > 1f + float.Epsilon;
            _trackX.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible) return;

            float trackW = _trackX.resolvedStyle.width;
            if (trackW < 1f) return;

            int totalBars = _element.BarCount;
            if (totalBars <= 0) return;

            float thumbRatio = Mathf.Clamp01(1f / args.ZoomX);
            float thumbW     = Mathf.Max(MinThumbPx, trackW * thumbRatio);
            float scrollable = trackW - thumbW;
            float maxPanX    = totalBars * (1f - 1f / args.ZoomX);
            float thumbL     = maxPanX > 0f ? scrollable * (args.PanX / maxPanX) : 0f;

            _thumbX.style.width = thumbW;
            _thumbX.style.left  = thumbL;
        }

        private void SyncY(ViewChangedEventArgs args)
        {
            bool visible = args.ZoomY > 1f + float.Epsilon;
            _trackY.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible) return;

            float trackH = _trackY.resolvedStyle.height;
            if (trackH < 1f) return;

            float thumbRatio = Mathf.Clamp01(1f / args.ZoomY);
            float thumbH     = Mathf.Max(MinThumbPx, trackH * thumbRatio);
            float scrollable = trackH - thumbH;
            float maxPanY    = args.ZoomY - 1f;
            float thumbT     = maxPanY > 0f ? scrollable * (args.PanY / maxPanY) : 0f;

            _thumbY.style.height = thumbH;
            _thumbY.style.top    = thumbT;
        }

        // ── Drag X ──────────────────────────────────────────────────────────

        private void OnThumbXDown(PointerDownEvent e)
        {
            if (e.button != 0) return;
            e.StopPropagation();
            _draggingX = true;
            _dragStartPointer = e.position.x;
            _dragStartPan = _element.ViewState.PanX;
            _thumbX.CapturePointer(e.pointerId);
            _thumbX.style.backgroundColor = ThumbDragColor;
        }

        private void OnThumbXMove(PointerMoveEvent e)
        {
            if (!_draggingX) return;

            float trackW   = _trackX.resolvedStyle.width;
            float thumbW   = _thumbX.resolvedStyle.width;
            float scrollable = trackW - thumbW;
            if (scrollable < 1f) return;

            float dx       = e.position.x - _dragStartPointer;
            int   total    = _element.BarCount;
            float maxPanX  = total * (1f - 1f / _element.ViewState.ZoomX);
            float newPanX  = _dragStartPan + dx / scrollable * maxPanX;

            _element.InternalSetPan(newPanX, _element.ViewState.PanY);
        }

        private void OnThumbXUp(PointerUpEvent e)
        {
            if (!_draggingX) return;
            _draggingX = false;
            _thumbX.ReleasePointer(e.pointerId);
            _thumbX.style.backgroundColor = ThumbColor;
        }

        // ── Drag Y ──────────────────────────────────────────────────────────

        private void OnThumbYDown(PointerDownEvent e)
        {
            if (e.button != 0) return;
            e.StopPropagation();
            _draggingY = true;
            _dragStartPointer = e.position.y;
            _dragStartPan = _element.ViewState.PanY;
            _thumbY.CapturePointer(e.pointerId);
            _thumbY.style.backgroundColor = ThumbDragColor;
        }

        private void OnThumbYMove(PointerMoveEvent e)
        {
            if (!_draggingY) return;

            float trackH    = _trackY.resolvedStyle.height;
            float thumbH    = _thumbY.resolvedStyle.height;
            float scrollable = trackH - thumbH;
            if (scrollable < 1f) return;

            float dy       = e.position.y - _dragStartPointer;
            float maxPanY  = _element.ViewState.ZoomY - 1f;
            float newPanY  = _dragStartPan + dy / scrollable * maxPanY;

            _element.InternalSetPan(_element.ViewState.PanX, newPanY);
        }

        private void OnThumbYUp(PointerUpEvent e)
        {
            if (!_draggingY) return;
            _draggingY = false;
            _thumbY.ReleasePointer(e.pointerId);
            _thumbY.style.backgroundColor = ThumbColor;
        }

        // ── Track click (page-jump) ─────────────────────────────────────────

        private void OnTrackXClick(PointerDownEvent e)
        {
            if (e.button != 0 || _draggingX) return;

            float trackW  = _trackX.resolvedStyle.width;
            float thumbW  = _thumbX.resolvedStyle.width;
            if (trackW < 1f) return;

            float localX   = e.localPosition.x;
            int   total    = _element.BarCount;
            float maxPanX  = total * (1f - 1f / _element.ViewState.ZoomX);
            float scrollable = trackW - thumbW;
            float targetPan = scrollable > 0f
                ? (localX - thumbW * 0.5f) / scrollable * maxPanX
                : 0f;

            _element.InternalSetPan(targetPan, _element.ViewState.PanY);
        }

        private void OnTrackYClick(PointerDownEvent e)
        {
            if (e.button != 0 || _draggingY) return;

            float trackH  = _trackY.resolvedStyle.height;
            float thumbH  = _thumbY.resolvedStyle.height;
            if (trackH < 1f) return;

            float localY   = e.localPosition.y;
            float maxPanY  = _element.ViewState.ZoomY - 1f;
            float scrollable = trackH - thumbH;
            float targetPan = scrollable > 0f
                ? (localY - thumbH * 0.5f) / scrollable * maxPanY
                : 0f;

            _element.InternalSetPan(_element.ViewState.PanX, targetPan);
        }
    }
}
