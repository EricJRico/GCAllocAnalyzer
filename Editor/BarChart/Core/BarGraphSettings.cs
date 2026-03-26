using System;
using UnityEngine;

namespace GCAllocBreakdown.BarChart.Core
{
    /// <summary>
    /// Behavioural settings for a <see cref="BarGraphElement"/>.
    /// Visual/appearance properties (colours, padding, spacing, font) are
    /// controlled via USS custom properties — see <c>BarGraph.uss</c>.
    /// </summary>
    [Serializable]
    public class BarGraphSettings
    {
        // ── Grid & Axes ─────────────────────────────────────────────────────
        [Header("Grid & Axes")]
        public bool ShowGrid      = true;
        public bool ShowAxes      = true;
        public int  GridLineCount = 5;

        // ── Value range ─────────────────────────────────────────────────────
        [Header("Value Range")]
        public float MinValue = 0f;
        /// <summary>0 = auto-scale to data maximum.</summary>
        public float MaxValue = 0f;

        // ── Interaction ─────────────────────────────────────────────────────
        [Header("Interaction")]
        public bool  EnableMouseZoomX = true;
        public bool  EnableMouseZoomY = false;
        public bool  EnableMousePan   = true;
        public bool  EnableYPan       = false;
        public bool  EnableSelection  = true;
        public bool  ClearBarsOnSegmentSelect  = true;
        public bool  ShowSegmentHighlightInLod = false;
        [Range(0.02f, 0.5f)]
        public float ZoomSpeed = 0.12f;

        // ── Labels ──────────────────────────────────────────────────────────
        [Header("Labels")]
        public int MaxXLabels = 12;
        public int MaxYLabels = 6;
    }
}
