using System;
using UnityEngine;

namespace GCAllocBreakdown.BarChart.Input
{
    /// <summary>
    /// Named input events for a <see cref="BarGraphElement"/>.
    ///
    /// An input source (e.g. <see cref="BarGraphUIToolkitInput"/>) fires these
    /// events in response to raw input.  Handlers subscribe to whichever events
    /// they care about.  Events with no subscribers are free null-delegate checks.
    ///
    /// Named EventBus rather than Actions to avoid confusion with Unity's Input
    /// System InputAction concept, which maps hardware inputs to named intents.
    /// </summary>
    public sealed class BarGraphEventBus
    {
        public event Action<Vector2>        Hovered;
        public event Action                 HoverLeft;
        public event Action<Vector2, bool>  SelectPressed;
        public event Action<Vector2>        SelectDragged;
        public event Action<Vector2>        SelectReleased;
        public event Action                 SelectCancelled;
        public event Action<Vector2>        PanPressed;
        public event Action<Vector2>        PanDragged;
        public event Action                 PanReleased;
        public event Action<float, Vector2> ZoomXRequested;
        public event Action<float, Vector2> ZoomYRequested;

        // Public so input sources in separate assemblies can fire without
        // requiring InternalsVisibleTo attributes.
        public void FireHovered(Vector2 pos)                        => Hovered?.Invoke(pos);
        public void FireHoverLeft()                                  => HoverLeft?.Invoke();
        public void FireSelectPressed(Vector2 pos, bool additive)   => SelectPressed?.Invoke(pos, additive);
        public void FireSelectDragged(Vector2 pos)                  => SelectDragged?.Invoke(pos);
        public void FireSelectReleased(Vector2 pos)                 => SelectReleased?.Invoke(pos);
        public void FireSelectCancelled()                            => SelectCancelled?.Invoke();
        public void FirePanPressed(Vector2 pos)                     => PanPressed?.Invoke(pos);
        public void FirePanDragged(Vector2 pos)                     => PanDragged?.Invoke(pos);
        public void FirePanReleased()                                => PanReleased?.Invoke();
        public void FireZoomXRequested(float delta, Vector2 anchor) => ZoomXRequested?.Invoke(delta, anchor);
        public void FireZoomYRequested(float delta, Vector2 anchor) => ZoomYRequested?.Invoke(delta, anchor);
    }
}
