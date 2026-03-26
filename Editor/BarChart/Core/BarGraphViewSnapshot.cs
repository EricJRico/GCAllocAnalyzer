using System;

namespace GCAllocBreakdown.BarChart.Core
{
    /// <summary>
    /// Lightweight serializable snapshot of a <see cref="BarGraphElement"/>'s view state.
    /// Held as a <c>[SerializeField]</c> on owners (MonoBehaviour / EditorWindow)
    /// so that zoom, pan, sort, and selection survive Unity domain reloads.
    ///
    /// Data arrays are deliberately excluded — they can be large (especially with
    /// deep stacking) and should be re-supplied by the owner after reload.
    /// </summary>
    [Serializable]
    public struct BarGraphViewSnapshot
    {
        // ── Zoom / pan ──────────────────────────────────────────────────────
        public float    ZoomX;
        public float    ZoomY;
        public float    PanX;
        public float    PanY;

        // ── Sort ────────────────────────────────────────────────────────────
        public SortMode SortMode;
        public bool     SortDescending;

        // ── Selection / focus ───────────────────────────────────────────────
        public int      FocusedBarIndex;
        /// <summary>
        /// <see cref="ChartViewState.SelectedBars"/> serialized as a flat array
        /// because <c>HashSet&lt;int&gt;</c> is not Unity-serializable.
        /// </summary>
        public int[]    SelectedBars;

        // ── Segment selection ─────────────────────────────────────────────
        public int      SelectedSegmentBar;
        public int      SelectedSegmentIndex;

        // ── Tag highlight ─────────────────────────────────────────────────
        public int      HighlightedTag;

        // ── Validity ────────────────────────────────────────────────────────
        /// <summary>
        /// Distinguishes a real captured snapshot from a default-initialized struct.
        /// </summary>
        public bool     IsValid;
    }
}
