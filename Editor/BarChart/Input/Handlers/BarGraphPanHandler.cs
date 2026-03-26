using UnityEngine;
using GCAllocBreakdown.BarChart.Core;

namespace GCAllocBreakdown.BarChart.Input.Handlers
{
    /// <summary>
    /// Handles horizontal and vertical panning.
    /// Subscribes to pan actions from <see cref="BarGraphEventBus"/>.
    /// </summary>
    public sealed class BarGraphPanHandler : IBarGraphHandler
    {
        private BarGraphElement _element;

        private Vector2 _dragStart;
        private float   _startPanX;
        private float   _startPanY;

        public void Register(BarGraphEventBus actions, BarGraphElement element)
        {
            _element = element;
            actions.PanPressed  += OnPressed;
            actions.PanDragged  += OnDragged;
            actions.PanReleased += OnReleased;
        }

        public void Unregister(BarGraphEventBus actions)
        {
            actions.PanPressed  -= OnPressed;
            actions.PanDragged  -= OnDragged;
            actions.PanReleased -= OnReleased;
            _element = null;
        }

        private void OnPressed(Vector2 pos)
        {
            _dragStart = pos;
            _startPanX = _element.ViewState.PanX;
            _startPanY = _element.ViewState.PanY;
        }

        private void OnDragged(Vector2 pos)
        {
            Vector2 panDelta = _element.ScreenDeltaToPanDelta(pos - _dragStart);
            float newPanX    = _startPanX + panDelta.x;
            float newPanY    = _startPanY + (_element.Settings.EnableYPan ? panDelta.y : 0f);

            _element.InternalSetPan(newPanX, newPanY);
        }

        private void OnReleased() { }
    }
}
