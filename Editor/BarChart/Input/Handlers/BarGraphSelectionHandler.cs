using System;
using UnityEngine;
using GCAllocBreakdown.BarChart.Core;
using GCAllocBreakdown.BarChart.Events;

namespace GCAllocBreakdown.BarChart.Input.Handlers
{
    /// <summary>
    /// Handles click-to-select and rubber-band drag selection.
    /// Subscribes to select actions from <see cref="BarGraphEventBus"/>.
    ///
    /// Owns the input interpretation events that were previously on
    /// <see cref="BarGraphElement"/>. Consumers subscribe to these
    /// events on the handler, not the element.
    /// </summary>
    public sealed class BarGraphSelectionHandler : IBarGraphHandler
    {
        BarGraphElement _element;

        Vector2 _dragStart;
        bool    _dragging;
        bool    _additive;
        int     _clickedBar;

        /// <summary>Fired when a bar is clicked (single click, not drag).</summary>
        public event Action<BarClickedEventArgs> BarClicked;

        /// <summary>Fired when a segment is clicked in a multi-segment bar.</summary>
        public event Action<SegmentEventArgs> SegmentClicked;

        /// <summary>Fired when a rubber-band drag-select gesture completes.</summary>
        public event Action<DragCompletedEventArgs> DragCompleted;

        public void Register(BarGraphEventBus actions, BarGraphElement element)
        {
            _element = element;
            actions.SelectPressed   += OnPressed;
            actions.SelectDragged   += OnDragged;
            actions.SelectReleased  += OnReleased;
            actions.SelectCancelled += OnCancelled;
        }

        public void Unregister(BarGraphEventBus actions)
        {
            actions.SelectPressed   -= OnPressed;
            actions.SelectDragged   -= OnDragged;
            actions.SelectReleased  -= OnReleased;
            actions.SelectCancelled -= OnCancelled;
            _element = null;
        }

        void OnPressed(Vector2 pos, bool additive)
        {
            _dragging   = false;
            _additive   = additive;
            _dragStart  = pos;
            _clickedBar = _element.HitTestBar(pos);
        }

        void OnDragged(Vector2 pos)
        {
            _dragging = true;
            _element.UpdateDragRect(MakeRect(_dragStart, pos));
        }

        void OnReleased(Vector2 pos)
        {
            if (_dragging)
            {
                Rect rect = MakeRect(_dragStart, pos);
                _element.CommitDragSelection(rect, _additive);

                DragCompleted?.Invoke(new DragCompletedEventArgs(
                    rect, _element.ViewState.SelectedBars));
            }
            else
            {
                var vs = _element.ViewState;

                if (vs.HoveredSegmentBar >= 0 && vs.HoveredSegmentIndex >= 0
                    && _element.GetSegmentCount(vs.HoveredSegmentBar) > 1)
                {
                    int barIdx = vs.HoveredSegmentBar;
                    int segIdx = vs.HoveredSegmentIndex;
                    _element.SelectSegment(barIdx, segIdx);

                    ref readonly BarEntry bar = ref _element.Bars[barIdx];
                    ref readonly BarSegment seg = ref _element.Segments[bar.SegmentStart + segIdx];
                    int dispIdx = barIdx < vs.DataToDisplay.Length
                        ? vs.DataToDisplay[barIdx] : barIdx;

                    SegmentClicked?.Invoke(new SegmentEventArgs
                    {
                        BarDataIndex    = barIdx,
                        BarDisplayIndex = dispIdx,
                        SegmentIndex    = segIdx,
                        Tag             = seg.Tag,
                        Value           = seg.Value,
                        Color           = seg.Color,
                        LocalPosition   = pos,
                    });
                }
                else
                {
                    _element.ToggleBar(_clickedBar, _additive);

                    if (_clickedBar >= 0)
                    {
                        int dispIdx = _clickedBar < vs.DataToDisplay.Length
                            ? vs.DataToDisplay[_clickedBar] : _clickedBar;
                        float val = _element.Bars[_clickedBar].TotalValue;
                        BarClicked?.Invoke(new BarClickedEventArgs(
                            _clickedBar, dispIdx, val, pos));
                    }
                }
            }

            _dragging = false;
        }

        void OnCancelled()
        {
            if (_dragging)
                _element.CancelDrag();
            _dragging = false;
        }

        static Rect MakeRect(Vector2 a, Vector2 b) => new Rect(
            Mathf.Min(a.x, b.x),
            Mathf.Min(a.y, b.y),
            Mathf.Abs(b.x - a.x),
            Mathf.Abs(b.y - a.y));
    }
}
