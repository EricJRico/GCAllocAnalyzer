using System;
using UnityEngine;

namespace GCAllocBreakdown.BarChart.Core
{
    /// <summary>
    /// Describes a single bar column.
    /// Segments are stored in a shared flat array; <see cref="SegmentStart"/>
    /// and <see cref="SegmentCount"/> index into it.
    /// </summary>
    [Serializable]
    public readonly struct BarEntry
    {
        /// <summary>Index into <c>ChartDataModel.Segments</c>.</summary>
        public readonly int SegmentStart;

        /// <summary>Number of stacked segments in this bar (≥ 1).</summary>
        public readonly int SegmentCount;

        /// <summary>Pre-computed sum of all segment values (used for sorting and normalisation).</summary>
        public readonly float TotalValue;

        /// <summary>Optional short label shown on the X-axis.</summary>
        public readonly string Label;

        // ── Convenience constructors ──────────────────────────────────────────

        /// <param name="segmentStart">Index into the flat segments array.</param>
        /// <param name="segmentCount">Number of segments.</param>
        /// <param name="totalValue">Pre-computed total (caller must keep this in sync).</param>
        /// <param name="label">X-axis label (may be null).</param>
        public BarEntry(int segmentStart, int segmentCount, float totalValue, string label = null)
        {
            SegmentStart  = segmentStart;
            SegmentCount  = Math.Max(1, segmentCount);
            TotalValue    = totalValue;
            Label         = label;
        }

        // ── Flat (single-segment) helpers ─────────────────────────────────────

        /// <summary>
        /// Build a single-segment entry that hasn't been assigned a slot yet.
        /// Used by <see cref="ChartDataModel"/> helpers internally.
        /// </summary>
        internal static BarEntry Placeholder(float value, string label) =>
            new BarEntry(0, 1, value, label);
    }
}
