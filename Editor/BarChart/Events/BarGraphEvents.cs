using System.Collections.Generic;
using GCAllocBreakdown.BarChart.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace GCAllocBreakdown.BarChart.Events
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Event arg structs (all readonly, live on the stack → zero heap alloc)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Fired when a bar is clicked or activated via keyboard (Space/Return).</summary>
    public readonly struct BarClickedEventArgs
    {
        /// <summary>Index in the original data array.</summary>
        public readonly int DataIndex;

        /// <summary>Index in the current display order (differs from DataIndex when sorted).</summary>
        public readonly int DisplayIndex;

        /// <summary>Total bar value (sum of all segments).</summary>
        public readonly float TotalValue;

        /// <summary>Mouse position in element-local space at click time.</summary>
        public readonly Vector2 LocalPosition;

        public BarClickedEventArgs(int dataIdx, int displayIdx, float value, Vector2 pos)
        {
            DataIndex     = dataIdx;
            DisplayIndex  = displayIdx;
            TotalValue    = value;
            LocalPosition = pos;
        }
    }

    /// <summary>Fired when the selection set changes.</summary>
    public readonly struct SelectionChangedEventArgs
    {
        /// <summary>Snapshot of all currently-selected data indices.</summary>
        public readonly IReadOnlyCollection<int> SelectedDataIndices;

        public SelectionChangedEventArgs(IReadOnlyCollection<int> selected)
        {
            SelectedDataIndices = selected;
        }
    }

    /// <summary>Fired when a drag-selection gesture is completed.</summary>
    public readonly struct DragCompletedEventArgs
    {
        /// <summary>The final drag rectangle in element-local pixel space.</summary>
        public readonly Rect DragRect;

        /// <summary>Data indices of all bars that fell within the drag rectangle.</summary>
        public readonly IReadOnlyCollection<int> SelectedDataIndices;

        public DragCompletedEventArgs(Rect rect, IReadOnlyCollection<int> selected)
        {
            DragRect            = rect;
            SelectedDataIndices = selected;
        }
    }

    /// <summary>Fired when the bar under the cursor changes.</summary>
    public readonly struct HoverChangedEventArgs
    {
        /// <summary>Data index of the newly-hovered bar, or -1 when the cursor leaves all bars.</summary>
        public readonly int DataIndex;

        /// <summary>Total value of the hovered bar, or 0 when DataIndex == -1.</summary>
        public readonly float TotalValue;

        public HoverChangedEventArgs(int dataIndex, float totalValue)
        {
            DataIndex  = dataIndex;
            TotalValue = totalValue;
        }
    }

    /// <summary>Fired when zoom, pan, or sort changes.</summary>
    public readonly struct ViewChangedEventArgs
    {
        public readonly float ZoomX;
        public readonly float ZoomY;
        public readonly float PanX;
        public readonly float PanY;
        public readonly SortMode SortMode;
        public readonly bool SortDescending;

        public ViewChangedEventArgs(float zx, float zy, float px, float py,
            SortMode sortMode = SortMode.None, bool sortDescending = true)
        {
            ZoomX = zx; ZoomY = zy; PanX = px; PanY = py;
            SortMode = sortMode; SortDescending = sortDescending;
        }
    }

}
