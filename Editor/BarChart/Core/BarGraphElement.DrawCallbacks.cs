using System;
using UnityEngine;

namespace GCAllocBreakdown.BarChart.Core
{
    /// <summary>
    /// Delegate for custom draw callbacks invoked during the
    /// <see cref="BarGraphElement"/> render pipeline.
    /// The context struct is stack-allocated and must not be stored beyond the callback.
    /// </summary>
    public delegate void BarGraphDrawCallback(in BarGraphDrawContext ctx);

    /// <summary>
    /// Per-bar visual override returned by <see cref="BarGraphElement.BarVisualProvider"/>.
    /// All fields are nullable — <c>null</c> means "use default".
    /// </summary>
    public struct BarVisualOverride
    {
        /// <summary>Override the bar's fill colour (applies to all segments).</summary>
        public Color32? Color;

        /// <summary>
        /// Override colour used only during LOD rendering (bar stride &lt; 1 px).
        /// When set, takes priority over <see cref="Color"/> in the LOD path.
        /// Leave null to fall back to <see cref="Color"/> or the first segment colour.
        /// </summary>
        public Color32? LodColor;

        /// <summary>Override the bar's opacity multiplier (0–1).</summary>
        public float? Alpha;
    }

    public sealed partial class BarGraphElement
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Custom draw callbacks
        // ─────────────────────────────────────────────────────────────────────

        private BarGraphDrawCallback _onDrawBeforeBars;
        private BarGraphDrawCallback _onDrawAfterBars;
        private BarGraphDrawCallback _onDrawAfterChrome;

        /// <summary>
        /// Called after grid lines but before any bar geometry.
        /// Use for reference lines, threshold bands, background annotations.
        /// </summary>
        public BarGraphDrawCallback OnDrawBeforeBars
        {
            get => _onDrawBeforeBars;
            set { _onDrawBeforeBars = value; MarkDirtyRepaint(); }
        }

        /// <summary>
        /// Called after all bar geometry is flushed but before highlights and chrome.
        /// Use for overlaid annotations that sit on top of bars but under selection UI.
        /// </summary>
        public BarGraphDrawCallback OnDrawAfterBars
        {
            get => _onDrawAfterBars;
            set { _onDrawAfterBars = value; MarkDirtyRepaint(); }
        }

        /// <summary>
        /// Called after axes, highlights, and drag rect — the final draw hook.
        /// Use for annotation layers that must appear above everything.
        /// </summary>
        public BarGraphDrawCallback OnDrawAfterChrome
        {
            get => _onDrawAfterChrome;
            set { _onDrawAfterChrome = value; MarkDirtyRepaint(); }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Per-bar visual callback
        // ─────────────────────────────────────────────────────────────────────

        private Func<int, BarVisualOverride?> _barVisualProvider;

        /// <summary>
        /// Callback queried per visible bar during rendering.
        /// Receives the data index; return <c>null</c> to use default rendering,
        /// or a <see cref="BarVisualOverride"/> to customise that bar's appearance.
        /// Only called for bars that are actually visible on screen.
        /// <para>Zero-alloc in steady state — the nullable struct return is stack-allocated.</para>
        /// </summary>
        public Func<int, BarVisualOverride?> BarVisualProvider
        {
            get => _barVisualProvider;
            set { _barVisualProvider = value; MarkDirtyRepaint(); }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Per-bar tag highlight filter
        // ─────────────────────────────────────────────────────────────────────

        private Func<int, bool> _tagHighlightFilter;

        /// <summary>
        /// Optional filter queried per visible bar during tag highlight rendering
        /// (tint and outline). Receives the data index; return <c>true</c> to apply
        /// tag highlighting, <c>false</c> to skip.
        /// <para>When <c>null</c> (default), all bars participate in tag highlighting.</para>
        /// </summary>
        public Func<int, bool> TagHighlightFilter
        {
            get => _tagHighlightFilter;
            set { _tagHighlightFilter = value; MarkDirtyRepaint(); }
        }
    }
}
