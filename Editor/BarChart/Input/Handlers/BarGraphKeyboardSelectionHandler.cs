using System;
using UnityEngine;
using UnityEngine.UIElements;
using GCAllocBreakdown.BarChart.Core;
using GCAllocBreakdown.BarChart.Events;

namespace GCAllocBreakdown.BarChart.Input.Handlers
{
    /// <summary>
    /// Handles keyboard-driven selection: arrow keys to move focus,
    /// Space/Enter to activate, Ctrl+A to select all, Escape to clear.
    /// Fires <see cref="BarActivated"/> when Space/Enter is pressed on a focused bar.
    ///
    /// Separate from <see cref="BarGraphKeyboardNavigationHandler"/> which handles
    /// WASD/arrow pan and zoom (navigation, not selection).
    /// </summary>
    public sealed class BarGraphKeyboardSelectionHandler : IBarGraphHandler
    {
        BarGraphElement _element;

        /// <summary>
        /// Fired when the user presses Space or Enter on a focused bar.
        /// Consumers decide what "activate" means (e.g. navigate to frame, drill down).
        /// </summary>
        public event Action<BarClickedEventArgs> BarActivated;

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

            switch (evt.keyCode)
            {
                case KeyCode.RightArrow:
                    MoveFocus(+1, evt.shiftKey);
                    evt.StopPropagation();
                    break;

                case KeyCode.LeftArrow:
                    MoveFocus(-1, evt.shiftKey);
                    evt.StopPropagation();
                    break;

                case KeyCode.Home:
                    SetFocus(0, evt.shiftKey);
                    evt.StopPropagation();
                    break;

                case KeyCode.End:
                    SetFocus(_element.BarCount - 1, evt.shiftKey);
                    evt.StopPropagation();
                    break;

                case KeyCode.Return:
                case KeyCode.Space:
                    ActivateFocusedBar();
                    evt.StopPropagation();
                    break;

                case KeyCode.A when evt.ctrlKey:
                    _element.SelectAll();
                    evt.StopPropagation();
                    break;

                case KeyCode.Escape:
                    _element.ClearSelection();
                    evt.StopPropagation();
                    break;

                case KeyCode.R when evt.ctrlKey:
                    _element.ResetView();
                    evt.StopPropagation();
                    break;
            }
        }

        void MoveFocus(int delta, bool extend)
        {
            var vs = _element.ViewState;
            int cur = vs.FocusedBarIndex < 0 ? 0 : vs.FocusedBarIndex;
            SetFocus(Mathf.Clamp(cur + delta, 0, _element.BarCount - 1), extend);
        }

        void SetFocus(int dataIdx, bool extend)
        {
            _element.ViewState.FocusedBarIndex = dataIdx;
            if (extend)
                _element.SelectBar(dataIdx, additive: true);
            else
                _element.SelectBar(dataIdx, additive: false);
            _element.EnsureBarVisible(dataIdx);
        }

        void ActivateFocusedBar()
        {
            int dataIdx = _element.ViewState.FocusedBarIndex;
            if (dataIdx < 0 || dataIdx >= _element.BarCount) return;

            int displayIdx = dataIdx < _element.ViewState.DataToDisplay.Length
                ? _element.ViewState.DataToDisplay[dataIdx] : dataIdx;
            float val = _element.Bars[dataIdx].TotalValue;

            BarActivated?.Invoke(new BarClickedEventArgs(dataIdx, displayIdx, val, Vector2.zero));
        }
    }
}
