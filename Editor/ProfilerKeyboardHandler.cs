using System;
using UnityEngine;
using UnityEngine.UIElements;
using BarGraph.Core;
using BarGraph.Events;
using BarGraph.Input;

namespace GCAllocBreakdown.Editor
{
    // ═══════════════════════════════════════════════════
    //  PROFILER KEYBOARD HANDLER
    //  Arrow-key navigation between bars and segments.
    //  Left/Right → move to adjacent bar (display order).
    //  Up/Down    → move to adjacent segment within bar.
    //  Fires BarClicked / SegmentClicked so the controller
    //  can reuse its existing click logic.
    // ═══════════════════════════════════════════════════

    internal sealed class ProfilerKeyboardHandler : IBarGraphHandler
    {
        BarGraphElement _element;

        /// <summary>Fired when the user arrows to a new bar.</summary>
        public event Action<BarClickedEventArgs> BarClicked;

        /// <summary>Fired when the user arrows to a new segment within the current bar.</summary>
        public event Action<SegmentEventArgs> SegmentClicked;

        public void Register(BarGraphEventBus actions, BarGraphElement element)
        {
            _element = element;
            _element.RegisterCallback<KeyDownEvent>(OnKeyDown);
        }

        public void Unregister(BarGraphEventBus actions)
        {
            _element.UnregisterCallback<KeyDownEvent>(OnKeyDown);
            _element = null;
        }

        void OnKeyDown(KeyDownEvent evt)
        {
            if (_element.BarCount == 0) return;
            if (evt.ctrlKey || evt.altKey || evt.shiftKey) return;

            switch (evt.keyCode)
            {
                case KeyCode.LeftArrow:  MoveBar(-1); evt.StopPropagation(); break;
                case KeyCode.RightArrow: MoveBar(+1); evt.StopPropagation(); break;
                case KeyCode.UpArrow:    MoveSegment(+1); evt.StopPropagation(); break;
                case KeyCode.DownArrow:  MoveSegment(-1); evt.StopPropagation(); break;
            }
        }

        void MoveBar(int delta)
        {
            var vs = _element.ViewState;
            int barCount = _element.BarCount;

            int curDisplay;
            if (vs.SelectedSegmentBar >= 0 && vs.SelectedSegmentBar < barCount)
                curDisplay = vs.DataToDisplay[vs.SelectedSegmentBar];
            else
                curDisplay = delta > 0 ? -1 : barCount;

            int newDisplay = Mathf.Clamp(curDisplay + delta, 0, barCount - 1);
            int newData = vs.DisplayToData[newDisplay];

            float val = _element.Bars[newData].TotalValue;
            _element.EnsureBarVisible(newData);

            BarClicked?.Invoke(new BarClickedEventArgs(newData, newDisplay, val, Vector2.zero));
        }

        void MoveSegment(int delta)
        {
            var vs = _element.ViewState;
            int barIdx = vs.SelectedSegmentBar;
            if (barIdx < 0 || barIdx >= _element.BarCount) return;

            ref readonly BarEntry bar = ref _element.Bars[barIdx];
            if (bar.SegmentCount <= 1) return;

            int curSeg = vs.SelectedSegmentIndex;
            int newSeg = Mathf.Clamp(curSeg + delta, 0, bar.SegmentCount - 1);
            if (newSeg == curSeg) return;

            _element.SelectSegment(barIdx, newSeg);

            ref readonly BarSegment seg = ref _element.Segments[bar.SegmentStart + newSeg];
            int dispIdx = barIdx < vs.DataToDisplay.Length
                ? vs.DataToDisplay[barIdx] : barIdx;

            SegmentClicked?.Invoke(new SegmentEventArgs
            {
                BarDataIndex    = barIdx,
                BarDisplayIndex = dispIdx,
                SegmentIndex    = newSeg,
                Tag             = seg.Tag,
                Value           = seg.Value,
                Color           = seg.Color,
                LocalPosition   = Vector2.zero,
            });
        }
    }
}
