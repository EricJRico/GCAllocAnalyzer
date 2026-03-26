using System.Collections.Generic;
using UnityEngine;

namespace GCAllocBreakdown.BarChart.Core
{
    /// <summary>
    /// All mutable view and interaction state for one <see cref="BarGraphElement"/>.
    ///
    /// This struct is the single source of truth that the renderer reads.
    /// Manipulators and keyboard handlers write to it through the element's
    /// internal <c>Apply*</c> methods (which also call <c>MarkDirtyRepaint()</c>).
    /// </summary>
    public sealed class ChartViewState
    {
        // ── Zoom / pan (per-axis, float data-space) ───────────────────────────

        /// <summary>
        /// Horizontal zoom.  1 = all bars visible, > 1 = zoomed in.
        /// <para>Clamped to [<see cref="MinZoomX"/>..<see cref="MaxZoomX"/>].</para>
        /// </summary>
        public float ZoomX = 1f;

        /// <summary>
        /// Vertical zoom.  1 = full Y range visible.
        /// <para>Clamped to [<see cref="MinZoomY"/>..<see cref="MaxZoomY"/>].</para>
        /// </summary>
        public float ZoomY = 1f;

        /// <summary>
        /// Fractional horizontal scroll in data-space bar units.
        /// A value of 2.5 means the viewport starts halfway through the third bar.
        /// Sub-pixel precision is preserved all the way to Painter2D.
        /// </summary>
        public float PanX = 0f;

        /// <summary>
        /// Vertical scroll offset as a fraction of the full Y range [0..1].
        /// 0 = bottom of data visible, 1 = top.
        /// </summary>
        public float PanY = 0f;

        public float MinZoomX =  1.0f;   // 1 = all bars fit; can't zoom out further
        public float MaxZoomX = 50.0f;
        public float MinZoomY =  0.1f;
        public float MaxZoomY = 20.0f;

        // ── Interaction state ─────────────────────────────────────────────────

        /// <summary>Data index of the bar currently under the cursor, or -1.</summary>
        public int HoveredBarIndex = -1;

        /// <summary>Data index of the bar containing the hovered segment, or -1.</summary>
        public int HoveredSegmentBar = -1;

        /// <summary>Segment index within the hovered bar's segment array, or -1.</summary>
        public int HoveredSegmentIndex = -1;

        /// <summary>Data index of the keyboard-focused bar, or -1.</summary>
        public int FocusedBarIndex = -1;

        /// <summary>Active drag-selection rectangle in element-local pixels, or Rect.zero.</summary>
        public Rect DragRect = Rect.zero;

        /// <summary>True while the user is actively dragging to select.</summary>
        public bool IsDragSelecting = false;

        // ── Selection set ─────────────────────────────────────────────────────

        /// <summary>Data indices of all selected bars.  HashSet for O(1) Contains.</summary>
        public readonly HashSet<int> SelectedBars = new HashSet<int>();

        /// <summary>Data index of the bar containing the selected segment, or -1.</summary>
        public int SelectedSegmentBar = -1;

        /// <summary>Segment index within the selected bar's segment array, or -1.</summary>
        public int SelectedSegmentIndex = -1;

        /// <summary>Tag value to highlight across all bars, or -1 for none.</summary>
        public int HighlightedTag = -1;

        // ── Sort ──────────────────────────────────────────────────────────────

        public SortMode SortMode = SortMode.None;
        public bool SortDescending = true;

        /// <summary>
        /// Maps display index → data index.  Rebuilt lazily when <see cref="SortDirty"/> is true.
        /// </summary>
        public int[] DisplayToData = new int[64];

        /// <summary>Maps data index → display index (inverse of <see cref="DisplayToData"/>).</summary>
        public int[] DataToDisplay = new int[64];

        public bool SortDirty = true;

        // ── Helpers ───────────────────────────────────────────────────────────

        public void ResetZoomPan()
        {
            ZoomX = 1f;
            ZoomY = 1f;
            PanX  = 0f;
            PanY  = 0f;
        }

        public void ClampPan(float visibleBars, float totalBars, float zoomY)
        {
            PanX = Mathf.Clamp(PanX, 0f, Mathf.Max(0f, totalBars - visibleBars));
            // maxPanY = zoomY - 1 so the tallest bar can always reach the top edge.
            // Derivation: to place bar top (value=maxY) at plotY2-plotH:
            //   yBottom - maxY*plotH/maxY*zoomY = plotY2-plotH
            //   plotY2 + PanY*plotH - zoomY*plotH = plotY2-plotH
            //   PanY = zoomY - 1
            PanY = Mathf.Clamp(PanY, 0f, Mathf.Max(0f, zoomY - 1f));
        }

        public void EnsureSortCapacity(int count)
        {
            if (DisplayToData.Length < count)
            {
                int n = System.Math.Max(DisplayToData.Length * 2, count);
                DisplayToData = new int[n];
                DataToDisplay = new int[n];
            }
        }
    }

    /// <summary>Controls how bars are ordered in the display.</summary>
    public enum SortMode
    {
        /// <summary>Preserve the original data order.</summary>
        None,
        /// <summary>Order by total bar value (ascending or descending per <c>SortDescending</c>).</summary>
        ByValue,
    }
}
