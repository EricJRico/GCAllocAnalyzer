using UnityEngine;
using GCAllocBreakdown.BarChart.Core;
using GCAllocBreakdown.BarChart.Input;

namespace GCAllocBreakdown.BarChart.Input.Handlers
{
    /// <summary>
    /// Handles X and Y axis zoom anchored to the cursor position.
    /// Subscribes to zoom actions from <see cref="BarGraphEventBus"/>.
    /// </summary>
    public sealed class BarGraphZoomHandler : IBarGraphHandler
    {
        private BarGraphElement _element;

        public void Register(BarGraphEventBus actions, BarGraphElement element)
        {
            _element = element;
            actions.ZoomXRequested += OnZoomX;
            actions.ZoomYRequested += OnZoomY;
        }

        public void Unregister(BarGraphEventBus actions)
        {
            actions.ZoomXRequested -= OnZoomX;
            actions.ZoomYRequested -= OnZoomY;
            _element = null;
        }

        private void OnZoomX(float delta, Vector2 anchor)
        {
            if (!_element.Settings.EnableMouseZoomX) return;

            var vs = _element.ViewState;
            float factor  = 1f + delta * _element.Settings.ZoomSpeed;
            float newZoom = Mathf.Clamp(vs.ZoomX * factor, vs.MinZoomX, vs.MaxZoomX);

            // Anchor the bar column under the cursor so it stays stationary
            float barStride = _element.GetBarStrideBase();
            float localX    = anchor.x - _element.VisPaddingLeft;
            float dataX     = vs.PanX + localX / (barStride * vs.ZoomX);
            float newPanX   = dataX - localX / (barStride * newZoom);

            _element.InternalSetZoom(newZoom, vs.ZoomY, newPanX, vs.PanY);
        }

        private void OnZoomY(float delta, Vector2 anchor)
        {
            if (!_element.Settings.EnableMouseZoomY) return;

            var vs = _element.ViewState;
            float factor  = 1f + delta * _element.Settings.ZoomSpeed;
            float newZoom = Mathf.Clamp(vs.ZoomY * factor, vs.MinZoomY, vs.MaxZoomY);

            // Anchor the Y value under the cursor so it stays stationary
            float plotH  = _element.GetPlotHeight();
            float plotY2 = _element.contentRect.height - _element.VisPaddingBottom;
            float t      = Mathf.Clamp01((plotY2 - anchor.y) / Mathf.Max(1f, plotH));
            float newPanY = newZoom * (vs.PanY + t) / vs.ZoomY - t;

            _element.InternalSetZoom(vs.ZoomX, newZoom, vs.PanX, newPanY);
        }
    }
}
