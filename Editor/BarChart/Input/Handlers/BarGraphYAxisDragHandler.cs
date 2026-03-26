using UnityEngine;
using UnityEngine.UIElements;
using GCAllocBreakdown.BarChart.Core;

namespace GCAllocBreakdown.BarChart.Input.Handlers
{
    /// <summary>
    /// Drag the Y-axis area (left padding) vertically to zoom the Y axis.
    /// Drag up to zoom in (stretch bars taller), drag down to zoom out.
    /// The Y value under the initial pointer position stays anchored.
    ///
    /// Registers pointer callbacks directly on the element with
    /// <see cref="TrickleDown.TrickleDown"/> so left-clicks in the left
    /// padding are intercepted before the input source classifies them as
    /// selection gestures.
    /// </summary>
    public sealed class BarGraphYAxisDragHandler : IBarGraphHandler
    {
        BarGraphElement _element;
        bool   _active;
        int    _pointerId;
        float  _startY;
        float  _startZoomY;
        float  _startPanY;

        const float k_Sensitivity = 3f;

        public void Register(BarGraphEventBus actions, BarGraphElement element)
        {
            _element = element;
            _element.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            _element.RegisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
            _element.RegisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
            _element.RegisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
        }

        public void Unregister(BarGraphEventBus actions)
        {
            _element.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            _element.UnregisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
            _element.UnregisterCallback<PointerUpEvent>(OnPointerUp, TrickleDown.TrickleDown);
            _element.UnregisterCallback<PointerCaptureOutEvent>(OnCaptureOut);
            _element = null;
        }

        void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button != (int)MouseButton.LeftMouse) return;

            Vector2 pos = (Vector2)evt.localPosition;
            if (pos.x >= _element.VisPaddingLeft) return;

            _active     = true;
            _pointerId  = evt.pointerId;
            _startY     = pos.y;
            _startZoomY = _element.ViewState.ZoomY;
            _startPanY  = _element.ViewState.PanY;

            _element.CapturePointer(evt.pointerId);
            evt.StopImmediatePropagation();
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_active || evt.pointerId != _pointerId) return;

            float dy    = ((Vector2)evt.localPosition).y - _startY;
            float plotH = _element.GetPlotHeight();

            // Negate dy: drag up (negative screen-space dy) → zoom in (factor > 1)
            float factor   = Mathf.Exp(-dy / Mathf.Max(1f, plotH) * k_Sensitivity);
            float newZoomY = Mathf.Clamp(
                _startZoomY * factor,
                _element.ViewState.MinZoomY,
                _element.ViewState.MaxZoomY);

            // Anchor the Y value at the drag-start point so it stays stationary.
            float plotY2  = _element.contentRect.height - _element.VisPaddingBottom;
            float t       = Mathf.Clamp01((plotY2 - _startY) / Mathf.Max(1f, plotH));
            float newPanY = newZoomY * (_startPanY + t) / _startZoomY - t;

            _element.InternalSetZoom(
                _element.ViewState.ZoomX, newZoomY,
                _element.ViewState.PanX,  newPanY);

            evt.StopImmediatePropagation();
        }

        void OnPointerUp(PointerUpEvent evt)
        {
            if (!_active || evt.pointerId != _pointerId) return;

            _active = false;
            _element.ReleasePointer(evt.pointerId);
            evt.StopImmediatePropagation();
        }

        void OnCaptureOut(PointerCaptureOutEvent evt)
        {
            _active = false;
        }
    }
}
