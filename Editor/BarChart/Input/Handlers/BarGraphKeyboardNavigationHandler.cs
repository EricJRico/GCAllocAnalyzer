using UnityEngine;
using UnityEngine.UIElements;
using GCAllocBreakdown.BarChart.Core;

namespace GCAllocBreakdown.BarChart.Input.Handlers
{
    /// <summary>
    /// Perfetto-style WASD keyboard navigation.
    ///
    ///   W  — Zoom in  (X axis, centered on visible range)
    ///   S  — Zoom out (X axis, centered on visible range)
    ///   A  — Pan left
    ///   D  — Pan right
    ///
    /// Opt-in: add via <see cref="BarGraphElement.AddHandler"/>.
    /// Held keys trigger continuous zoom/pan via key repeat.
    /// </summary>
    public sealed class BarGraphKeyboardNavigationHandler : IBarGraphHandler
    {
        private BarGraphElement _element;

        public void Register(BarGraphEventBus actions, BarGraphElement element)
        {
            _element = element;
            _element.RegisterCallback<KeyDownEvent>(OnKeyDown);
        }

        public void Unregister(BarGraphEventBus actions)
        {
            _element?.UnregisterCallback<KeyDownEvent>(OnKeyDown);
            _element = null;
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            // Don't intercept modified keys (Ctrl+A = Select All, etc.)
            if (evt.ctrlKey || evt.commandKey || evt.altKey) return;

            // Match both keyCode and character — in Unity runtime, unmodified
            // letter keys may arrive with keyCode == KeyCode.None and only
            // the character field set.
            int action = ClassifyKey(evt);
            if (action == 0) return;

            switch (action)
            {
                case 1: ZoomX(+1); break;  // W
                case 2: ZoomX(-1); break;  // S
                case 3: PanX(-1);  break;  // A
                case 4: PanX(+1);  break;  // D
            }
            evt.StopPropagation();
        }

        // Returns 1=W 2=S 3=A 4=D 0=unhandled
        private static int ClassifyKey(KeyDownEvent evt)
        {
            switch (evt.keyCode)
            {
                case KeyCode.W: return 1;
                case KeyCode.S: return 2;
                case KeyCode.A: return 3;
                case KeyCode.D: return 4;
            }
            // Fallback: runtime may only set character for letter keys
            switch (evt.character)
            {
                case 'w': case 'W': return 1;
                case 's': case 'S': return 2;
                case 'a': case 'A': return 3;
                case 'd': case 'D': return 4;
            }
            return 0;
        }

        private void ZoomX(int direction)
        {
            var vs        = _element.ViewState;
            var settings  = _element.Settings;
            float factor  = direction > 0 ? (1f + settings.ZoomSpeed) : 1f / (1f + settings.ZoomSpeed);
            float newZoom = Mathf.Clamp(vs.ZoomX * factor, vs.MinZoomX, vs.MaxZoomX);

            // Anchor to the center of the visible range
            float barStride = _element.GetBarStrideBase();
            float plotW     = _element.GetPlotWidth();
            float centerX   = _element.VisPaddingLeft + plotW * 0.5f;
            float dataX     = vs.PanX + centerX / (barStride * vs.ZoomX);
            float newPanX   = dataX - centerX / (barStride * newZoom);

            _element.InternalSetZoom(newZoom, vs.ZoomY, newPanX, vs.PanY);
        }

        private void PanX(int direction)
        {
            var vs           = _element.ViewState;
            float stride     = _element.GetBarStride();
            float plotW      = _element.GetPlotWidth();
            float visibleBars = plotW / Mathf.Max(1f, stride);
            float panStep    = visibleBars * 0.1f;

            _element.InternalSetPan(vs.PanX + direction * panStep, vs.PanY);
        }
    }
}
