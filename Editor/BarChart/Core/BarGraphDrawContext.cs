using UnityEngine;
using UnityEngine.UIElements;

namespace GCAllocBreakdown.BarChart.Core
{
    /// <summary>
    /// Read-only context passed to custom draw callbacks during the
    /// <see cref="BarGraphElement"/> render pipeline.
    /// Stack-allocated — do not store references to this struct beyond the callback.
    /// </summary>
    public readonly struct BarGraphDrawContext
    {
        /// <summary>Painter2D for vector drawing (lines, strokes, fills).</summary>
        public readonly Painter2D Painter;

        /// <summary>
        /// MeshGenerationContext for direct mesh allocation.
        /// Available at all callback stages.
        /// </summary>
        public readonly MeshGenerationContext MeshContext;

        /// <summary>Plot area in element-local coordinates (excludes padding).</summary>
        public readonly Rect PlotRect;

        /// <summary>Current horizontal zoom level (1 = all bars fit).</summary>
        public readonly float ZoomX;

        /// <summary>Current vertical zoom level.</summary>
        public readonly float ZoomY;

        /// <summary>Horizontal pan offset in data-space bar units.</summary>
        public readonly float PanX;

        /// <summary>Vertical pan offset (0–1 normalized).</summary>
        public readonly float PanY;

        /// <summary>Effective maximum Y value used for vertical scaling.</summary>
        public readonly float EffectiveMaxY;

        /// <summary>
        /// Bar stride in pixels (slot width including spacing).
        /// Less than 1 when in LOD pixel-column mode.
        /// Zero when there are no bars.
        /// </summary>
        public readonly float BarStride;

        /// <summary>Bar body width in pixels (stride minus spacing gap).</summary>
        public readonly float BarWidth;

        /// <summary>First visible display index (inclusive). -1 when no bars.</summary>
        public readonly int StartDisplayIndex;

        /// <summary>Last visible display index (exclusive). -1 when no bars.</summary>
        public readonly int EndDisplayIndex;

        /// <summary>Total bar count in the dataset.</summary>
        public readonly int BarCount;

        internal BarGraphDrawContext(
            Painter2D painter, MeshGenerationContext mgc,
            Rect plotRect,
            float zoomX, float zoomY, float panX, float panY,
            float effectiveMaxY, float barStride, float barWidth,
            int startDisp, int endDisp, int barCount)
        {
            Painter           = painter;
            MeshContext        = mgc;
            PlotRect           = plotRect;
            ZoomX              = zoomX;
            ZoomY              = zoomY;
            PanX               = panX;
            PanY               = panY;
            EffectiveMaxY      = effectiveMaxY;
            BarStride          = barStride;
            BarWidth           = barWidth;
            StartDisplayIndex  = startDisp;
            EndDisplayIndex    = endDisp;
            BarCount           = barCount;
        }
    }
}
