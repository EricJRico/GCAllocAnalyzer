using UnityEngine;
using UnityEngine.UIElements;

namespace GCAllocBreakdown.BarChart.Input
{
    /// <summary>
    /// UIToolkit input source for <see cref="BarGraphElement"/>.
    ///
    /// Implements <see cref="IBarGraphInputSource"/> so <see cref="BarGraphElement.SetInputSource"/>
    /// injects the <see cref="BarGraphEventBus"/> automatically — callers never
    /// need to access the event bus directly.
    ///
    /// This is the only class that knows about mouse buttons and modifier keys.
    /// Works in both Runtime UIDocument and Editor EditorWindow contexts.
    ///
    /// Bindings
    /// ─────────
    ///  Left mouse        → select press / drag / release
    ///  Ctrl + left       → additive select
    ///  Alt + left        → pan (alternative to middle mouse)
    ///  Middle mouse      → pan
    ///  Scroll            → zoom X
    ///  Ctrl + Scroll     → zoom Y
    /// </summary>
    public sealed class BarGraphUIToolkitInput : Manipulator, IBarGraphInputSource
    {
        private BarGraphEventBus _events;

        private const float DragThreshold = 3f;

        // ── Select state ──────────────────────────────────────────────────────
        private Vector2 _selectStart;
        private bool    _selectDragging;
        private bool    _selectActive;
        private bool    _selectAdditive;

        // ── Pan state ─────────────────────────────────────────────────────────
        private bool _panActive;

        // ── IBarGraphInputSource ──────────────────────────────────────────────

        public void Initialize(BarGraphEventBus eventBus)
        {
            _events = eventBus;
        }

        // ── Manipulator lifecycle ─────────────────────────────────────────────

        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<PointerDownEvent>(OnPointerDown);
            target.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            target.RegisterCallback<PointerUpEvent>(OnPointerUp);
            target.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
            target.RegisterCallback<PointerLeaveEvent>(OnPointerLeave);
            target.RegisterCallback<WheelEvent>(OnWheel);
        }

        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<PointerDownEvent>(OnPointerDown);
            target.UnregisterCallback<PointerMoveEvent>(OnPointerMove);
            target.UnregisterCallback<PointerUpEvent>(OnPointerUp);
            target.UnregisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
            target.UnregisterCallback<PointerLeaveEvent>(OnPointerLeave);
            target.UnregisterCallback<WheelEvent>(OnWheel);
        }

        // ── Pointer down ──────────────────────────────────────────────────────

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (_events == null) return;
            Vector2 pos = (Vector2)evt.localPosition;

            if (IsPanGesture(evt))
            {
                _panActive = true;
                target.CapturePointer(evt.pointerId);
                _events.FirePanPressed(pos);
                evt.StopPropagation();
            }
            else if (IsSelectGesture(evt))
            {
                _selectActive   = true;
                _selectDragging = false;
                _selectAdditive = evt.ctrlKey;
                _selectStart    = pos;
                target.CapturePointer(evt.pointerId);
                target.Focus();
                _events.FireSelectPressed(pos, _selectAdditive);
                evt.StopPropagation();
            }
        }

        // ── Pointer move ──────────────────────────────────────────────────────

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_events == null) return;
            Vector2 pos = (Vector2)evt.localPosition;

            // Hover — only fire when no gesture is active and pointer is not captured.
            // Guarding on HasPointerCapture prevents spurious hover events while
            // panning or selecting (pointer may still move while captured).
            if (!_selectActive && !_panActive && !target.HasPointerCapture(evt.pointerId))
            {
                _events.FireHovered(pos);
                return;
            }

            if (!target.HasPointerCapture(evt.pointerId)) return;

            if (_panActive)
            {
                _events.FirePanDragged(pos);
                evt.StopPropagation();
            }
            else if (_selectActive)
            {
                if (!_selectDragging &&
                    Vector2.Distance(pos, _selectStart) > DragThreshold)
                    _selectDragging = true;

                if (_selectDragging)
                {
                    _events.FireSelectDragged(pos);
                    evt.StopPropagation();
                }
            }
        }

        // ── Pointer up ────────────────────────────────────────────────────────

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (_events == null) return;
            if (!target.HasPointerCapture(evt.pointerId)) return;

            Vector2 pos = (Vector2)evt.localPosition;

            if (_panActive)
            {
                _panActive = false;
                target.ReleasePointer(evt.pointerId);
                _events.FirePanReleased();
                evt.StopPropagation();
            }
            else if (_selectActive)
            {
                _selectActive   = false;
                _selectDragging = false;
                target.ReleasePointer(evt.pointerId);
                _events.FireSelectReleased(pos);
                evt.StopPropagation();
            }
        }

        // ── Pointer capture lost ──────────────────────────────────────────────

        private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            if (_events == null) return;

            if (_selectActive || _selectDragging)
            {
                _selectActive   = false;
                _selectDragging = false;
                _events.FireSelectCancelled();
            }

            if (_panActive)
            {
                _panActive = false;
                _events.FirePanReleased();
            }
        }

        // ── Pointer leave ─────────────────────────────────────────────────────

        private void OnPointerLeave(PointerLeaveEvent evt)
        {
            if (_events == null) return;
            if (!_selectActive && !_panActive)
                _events.FireHoverLeft();
        }

        // ── Wheel ─────────────────────────────────────────────────────────────

        private void OnWheel(WheelEvent evt)
        {
            if (_events == null) return;

            // delta.y positive = scroll down = zoom out → negative delta to handlers
            float delta    = evt.delta.y > 0f ? -1f : 1f;
            Vector2 anchor = (Vector2)evt.localMousePosition;

            if (evt.ctrlKey)
                _events.FireZoomYRequested(delta, anchor);
            else
                _events.FireZoomXRequested(delta, anchor);

            evt.StopPropagation();
        }

        // ── Gesture classification ────────────────────────────────────────────

        private static bool IsPanGesture(IPointerEvent evt)
            => evt.button == (int)MouseButton.MiddleMouse
            || (evt.button == (int)MouseButton.LeftMouse && evt.altKey);

        private static bool IsSelectGesture(IPointerEvent evt)
            => evt.button == (int)MouseButton.LeftMouse && !evt.altKey;
    }
}
