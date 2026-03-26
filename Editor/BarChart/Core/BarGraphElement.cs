using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using GCAllocBreakdown.BarChart.Events;
using GCAllocBreakdown.BarChart.Input;

namespace GCAllocBreakdown.BarChart.Core
{
    /// <summary>
    /// High-performance stacked vertical bar graph for Unity UI Toolkit.
    ///
    /// ── Performance model ────────────────────────────────────────────────────
    ///  • Bar geometry bypasses <c>Painter2D</c> entirely.  Quads are written
    ///    directly via <c>MeshGenerationContext.Allocate()</c> in chunks of up to
    ///    16 383 quads (65 532 vertices), with unlimited chunks per repaint.
    ///    No tessellation overhead, no 65 535-vertex ceiling.
    ///  • Segment-level LOD: sub-pixel segments are merged in a single O(n) pass,
    ///    capping output to ~plotHeight rects per bar regardless of segment count.
    ///  • Bar-level LOD: when bar slot width &lt; 1 screen pixel the renderer
    ///    switches to pixel-column mode — cost bounded by panel width, not bar count.
    ///  • Chrome (grid, axes, highlights, drag rect) still uses <c>Painter2D</c>
    ///    since vertex counts are trivially bounded.
    ///  • Backing arrays grow (double) but never shrink → zero GC in steady state.
    ///  • Label pool: fixed-size <see cref="Label"/> pool repositioned each repaint.
    ///  • Sort index is rebuilt lazily only when <see cref="ChartViewState.SortDirty"/> is set.
    ///
    /// ── Minimal usage ────────────────────────────────────────────────────────
    ///   var graph = new BarGraphElement();
    ///   graph.style.flexGrow = 1;
    ///   graph.SetData(new float[] { 12, 45, 7, 88, 33 });
    ///   rootElement.Add(graph);
    /// </summary>
    // [UxmlElement] is the Unity 6 replacement for the UxmlFactory/UxmlTraits pattern.
    // The old nested-class approach still compiles but emits CS0618 obsolete warnings;
    // using the attribute silences them and is forward-compatible.
    [UxmlElement]
    public sealed partial class BarGraphElement : VisualElement
    {
        // ─────────────────────────────────────────────────────────────────────
        //  USS custom style properties  (Painter2D chrome → read at style-resolve time)
        // ─────────────────────────────────────────────────────────────────────

        // Colors
        static readonly CustomStyleProperty<Color> k_BgColor              = new("--bar-graph-bg-color");
        static readonly CustomStyleProperty<Color> k_DefaultBarColor      = new("--bar-graph-default-bar-color");
        static readonly CustomStyleProperty<Color> k_AxisColor            = new("--bar-graph-axis-color");
        static readonly CustomStyleProperty<Color> k_GridLineColor        = new("--bar-graph-grid-line-color");
        static readonly CustomStyleProperty<Color> k_HoverTintColor       = new("--bar-graph-hover-tint-color");
        static readonly CustomStyleProperty<Color> k_SelectionFillColor   = new("--bar-graph-selection-fill-color");
        static readonly CustomStyleProperty<Color> k_SelectionRimColor    = new("--bar-graph-selection-rim-color");
        static readonly CustomStyleProperty<Color> k_SegSelectionColor    = new("--bar-graph-segment-selection-color");
        static readonly CustomStyleProperty<Color> k_DragRectFillColor    = new("--bar-graph-drag-rect-fill-color");
        static readonly CustomStyleProperty<Color> k_DragRectBorderColor  = new("--bar-graph-drag-rect-border-color");
        static readonly CustomStyleProperty<Color> k_FocusRimColor        = new("--bar-graph-focus-rim-color");
        static readonly CustomStyleProperty<Color> k_OverlayTint          = new("--bar-graph-overlay-tint");
        static readonly CustomStyleProperty<Color> k_TagHighlightTint     = new("--bar-graph-tag-highlight-tint");
        static readonly CustomStyleProperty<Color> k_TagHighlightOutline  = new("--bar-graph-tag-highlight-outline");

        // Floats
        static readonly CustomStyleProperty<float> k_SelectionRimWidth    = new("--bar-graph-selection-rim-width");
        static readonly CustomStyleProperty<float> k_SegSelectionWidth    = new("--bar-graph-segment-selection-width");
        static readonly CustomStyleProperty<float> k_OverlayOpacity       = new("--bar-graph-overlay-opacity");
        static readonly CustomStyleProperty<float> k_BarSpacingRatio      = new("--bar-graph-bar-spacing-ratio");
        static readonly CustomStyleProperty<float> k_MinBarWidth          = new("--bar-graph-min-bar-width");
        static readonly CustomStyleProperty<float> k_GridLineWidth        = new("--bar-graph-grid-line-width");
        static readonly CustomStyleProperty<float> k_AxisLineWidth        = new("--bar-graph-axis-line-width");
        static readonly CustomStyleProperty<float> k_PaddingLeft          = new("--bar-graph-padding-left");
        static readonly CustomStyleProperty<float> k_PaddingRight         = new("--bar-graph-padding-right");
        static readonly CustomStyleProperty<float> k_PaddingTop           = new("--bar-graph-padding-top");
        static readonly CustomStyleProperty<float> k_PaddingBottom        = new("--bar-graph-padding-bottom");
        static readonly CustomStyleProperty<float> k_LabelHeight          = new("--bar-graph-label-height");
        static readonly CustomStyleProperty<float> k_XLabelWidth          = new("--bar-graph-x-label-width");
        static readonly CustomStyleProperty<float> k_XLabelOffsetY        = new("--bar-graph-x-label-offset-y");
        static readonly CustomStyleProperty<float> k_YLabelGap            = new("--bar-graph-y-label-gap");
        static readonly CustomStyleProperty<float> k_DimOpacity           = new("--bar-graph-dim-opacity");
        static readonly CustomStyleProperty<float> k_TagHighlightOutlineWidth = new("--bar-graph-tag-highlight-outline-width");

        // ─────────────────────────────────────────────────────────────────────
        //  Resolved visual cache  (populated from USS, read by draw methods)
        // ─────────────────────────────────────────────────────────────────────

        private struct ResolvedVisuals
        {
            public Color BackgroundColor;
            public Color DefaultBarColor;
            public Color AxisColor;
            public Color GridLineColor;
            public Color HoverTintColor;
            public Color SelectionFillColor;
            public Color SelectionRimColor;
            public Color SegSelectionColor;
            public Color DragRectFillColor;
            public Color DragRectBorderColor;
            public Color FocusRimColor;
            public Color OverlayTint;
            public Color TagHighlightTint;
            public Color TagHighlightOutline;

            public float SelectionRimWidth;
            public float SegSelectionWidth;
            public float OverlayOpacity;
            public float BarSpacingRatio;
            public float MinBarWidthPx;
            public float GridLineWidth;
            public float AxisLineWidth;
            public float PaddingLeft, PaddingRight, PaddingTop, PaddingBottom;
            public float LabelHeight;
            public float XLabelWidth, XLabelOffsetY;
            public float YLabelGap;
            public float DimOpacity;
            public float TagHighlightOutlineWidth;
        }

        private ResolvedVisuals _vis = new ResolvedVisuals
        {
            // Defaults match original BarGraphSettings so the graph looks identical
            // before any USS is resolved.
            BackgroundColor    = new Color(0.07f, 0.07f, 0.10f, 1.00f),
            DefaultBarColor    = new Color(0.25f, 0.60f, 1.00f, 1.00f),
            AxisColor          = new Color(0.55f, 0.55f, 0.60f, 1.00f),
            GridLineColor      = new Color(0.18f, 0.18f, 0.24f, 1.00f),
            HoverTintColor     = new Color(1.00f, 1.00f, 1.00f, 0.18f),
            SelectionFillColor = new Color(1.00f, 1.00f, 1.00f, 0.22f),
            SelectionRimColor  = new Color(0.40f, 0.70f, 1.00f, 0.90f),
            SegSelectionColor  = new Color(1.00f, 0.80f, 0.20f, 1.00f),
            DragRectFillColor  = new Color(0.35f, 0.65f, 1.00f, 0.08f),
            DragRectBorderColor= new Color(0.35f, 0.65f, 1.00f, 0.60f),
            FocusRimColor      = new Color(1.00f, 0.80f, 0.20f, 1.00f),
            OverlayTint        = new Color(1.00f, 1.00f, 1.00f, 0.38f),
            TagHighlightTint   = new Color(1.00f, 1.00f, 1.00f, 0.22f),
            TagHighlightOutline = new Color(0f, 0f, 0f, 0f),

            SelectionRimWidth  = 1.5f,
            SegSelectionWidth  = 2.5f,
            OverlayOpacity     = 0.35f,
            BarSpacingRatio    = 0.12f,
            MinBarWidthPx      = 1f,
            GridLineWidth      = 1f,
            AxisLineWidth      = 1.5f,
            PaddingLeft        = 52f,
            PaddingRight       = 12f,
            PaddingTop         = 12f,
            PaddingBottom      = 32f,
            LabelHeight        = 18f,
            DimOpacity         = 1f,
            TagHighlightOutlineWidth = 0f,
            XLabelWidth        = 40f,
            XLabelOffsetY      = 3f,
            YLabelGap          = 4f,
        };

        // Default USS — loaded once, shared by all instances.
        private static StyleSheet s_defaultSheet;

        // ─────────────────────────────────────────────────────────────────────
        //  Core state
        // ─────────────────────────────────────────────────────────────────────

        private readonly ChartDataModel  _model    = new ChartDataModel();
        private readonly ChartViewState  _viewState = new ChartViewState();
        private          BarGraphSettings _settings  = new BarGraphSettings();

        // ── Formatters (pluggable, non-null defaulted) ────────────────────────
        private Func<float, string> _yFormatter = DefaultYFormatter;
        private Func<int,   string> _xFormatter = DefaultXFormatter;

        private static readonly Func<float, string> DefaultYFormatter = v =>
            Mathf.Abs(v) >= 1_000_000f ? $"{v / 1_000_000f:0.#}M" :
            Mathf.Abs(v) >= 1_000f     ? $"{v / 1_000f:0.#}K"      :
            Mathf.Abs(v) >= 100f       ? $"{v:0}"                   :
            Mathf.Abs(v) >= 10f        ? $"{v:0.#}"                 :
                                          $"{v:0.##}";

        private static readonly Func<int, string> DefaultXFormatter = i => i.ToString();

        // ── Derived bounds (recomputed after each data change) ────────────────
        private float _effectiveMaxY = 1f;

        // ─────────────────────────────────────────────────────────────────────
        //  Per-frame zero-GC render buffers
        // ─────────────────────────────────────────────────────────────────────

        // Bar quads written by DrawDirectBars / DrawLodBars, flushed via
        // MeshGenerationContext.Allocate().  Grows via doubling, never shrinks.
        private QuadData[] _quadBuf = new QuadData[4096];
        private int        _quadCount;

        // LOD pixel buffer (grows, never shrinks)
        private LodPixel[] _lodBuf = new LodPixel[2048];

        // Selection run buffer: pairs of (runStartDisp, runEndDispExclusive)
        private readonly List<int> _selectionRuns = new List<int>(32);

        // ─────────────────────────────────────────────────────────────────────
        //  Label overlay pool
        // ─────────────────────────────────────────────────────────────────────

        private readonly VisualElement _labelRoot;
        private readonly List<Label>   _yLabels = new List<Label>();
        private readonly List<Label>   _xLabels = new List<Label>();

        // Cached plot-area coords written by OnGenerateVisualContent so that
        // PositionLabels can be called from the scheduled callback (outside the
        // repaint pass) rather than from inside generateVisualContent.
        private float _lblPlotX, _lblPlotW, _lblPlotH, _lblPlotY2;
        private bool  _labelsDirty;

        // ─────────────────────────────────────────────────────────────────────
        //  Public events (Action<T> → zero-alloc dispatch with stack-only args)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Fired whenever the selection set changes (state notification).</summary>
        public event Action<SelectionChangedEventArgs> SelectionChanged;

        /// <summary>Fired when the bar under the cursor changes (or cursor leaves — state notification).</summary>
        public event Action<HoverChangedEventArgs>   HoverChanged;

        /// <summary>Fired after zoom or pan changes (state notification).</summary>
        public event Action<ViewChangedEventArgs>    ViewChanged;

        /// <summary>
        /// Fired when the segment under the cursor changes (or cursor leaves all segments).
        /// Args have <c>BarDataIndex == -1</c> when no segment is hovered.
        /// </summary>
        public event Action<SegmentEventArgs>        SegmentHoverChanged;

        // ── Input ─────────────────────────────────────────────────────────────

        private readonly BarGraphEventBus       _eventBus = new BarGraphEventBus();
        private          Manipulator            _inputSource;
        private readonly List<IBarGraphHandler> _handlers = new List<IBarGraphHandler>();

        // ─────────────────────────────────────────────────────────────────────
        //  Construction
        // ─────────────────────────────────────────────────────────────────────

        public BarGraphElement()
        {
            name           = "bar-graph";
            style.overflow = Overflow.Hidden;
            pickingMode    = PickingMode.Position;
            focusable      = true;
            tabIndex       = 0;

            // USS class names — connect selectors in BarGraph.uss
            AddToClassList("bar-graph");

            // Load default stylesheet (cached across all instances)
            if (s_defaultSheet == null)
                s_defaultSheet = Resources.Load<StyleSheet>("BarGraph");
            if (s_defaultSheet != null)
                styleSheets.Add(s_defaultSheet);

            _labelRoot = new VisualElement { name = "bar-graph__labels", pickingMode = PickingMode.Ignore };
            _labelRoot.AddToClassList("bar-graph__labels");
            _labelRoot.style.position = Position.Absolute;
            _labelRoot.style.left     = 0;
            _labelRoot.style.top      = 0;
            _labelRoot.style.right    = 0;
            _labelRoot.style.bottom   = 0;
            Add(_labelRoot);

            generateVisualContent += OnGenerateVisualContent;

            RegisterCallback<GeometryChangedEvent>(_ => { EnsureLabelPool(); MarkDirtyRepaint(); });
            RegisterCallback<AttachToPanelEvent>(_ =>
                schedule.Execute(FlushLabelPositions).Every(0));
            RegisterCallback<CustomStyleResolvedEvent>(_ => ResolveCustomStyles());

            // Keyboard selection extracted to BarGraphKeyboardSelectionHandler
            _model.DataChanged    += OnDataChanged;
            _model.OverlayChanged += OnOverlayChanged;
        }

        /// <summary>
        /// Reads USS custom properties into the <see cref="_vis"/> cache.
        /// Called once on attach and again whenever USS classes or stylesheets change.
        /// </summary>
        private void ResolveCustomStyles()
        {
            // Colors — skipped when a C# override is active for that property
            TryResolveColor(k_BgColor,             VisualProperty.BackgroundColor,     ref _vis.BackgroundColor);
            TryResolveColor(k_DefaultBarColor,     VisualProperty.DefaultBarColor,     ref _vis.DefaultBarColor);
            TryResolveColor(k_AxisColor,           VisualProperty.AxisColor,           ref _vis.AxisColor);
            TryResolveColor(k_GridLineColor,       VisualProperty.GridLineColor,       ref _vis.GridLineColor);
            TryResolveColor(k_HoverTintColor,      VisualProperty.HoverTintColor,      ref _vis.HoverTintColor);
            TryResolveColor(k_SelectionFillColor,  VisualProperty.SelectionFillColor,  ref _vis.SelectionFillColor);
            TryResolveColor(k_SelectionRimColor,   VisualProperty.SelectionRimColor,   ref _vis.SelectionRimColor);
            TryResolveColor(k_FocusRimColor,       VisualProperty.FocusRimColor,       ref _vis.FocusRimColor);

            // Segment selection inherits from focus rim color (the visible "selection"
            // outline on bars) unless explicitly overridden via C# or USS.
            if (!HasOverride(VisualProperty.SegSelectionColor))
            {
                _vis.SegSelectionColor = _vis.FocusRimColor;
                if (customStyle.TryGetValue(k_SegSelectionColor, out var segC))
                    _vis.SegSelectionColor = segC;
            }

            TryResolveColor(k_DragRectFillColor,   VisualProperty.DragRectFillColor,   ref _vis.DragRectFillColor);
            TryResolveColor(k_DragRectBorderColor,  VisualProperty.DragRectBorderColor, ref _vis.DragRectBorderColor);
            TryResolveColor(k_OverlayTint,          VisualProperty.OverlayTint,         ref _vis.OverlayTint);
            TryResolveColor(k_TagHighlightTint,     VisualProperty.TagHighlightTint,    ref _vis.TagHighlightTint);
            TryResolveColor(k_TagHighlightOutline,  VisualProperty.TagHighlightOutline, ref _vis.TagHighlightOutline);

            // Floats — skipped when a C# override is active for that property
            TryResolveFloat(k_SelectionRimWidth,   VisualProperty.SelectionRimWidth,   ref _vis.SelectionRimWidth);
            TryResolveFloat(k_SegSelectionWidth,   VisualProperty.SegSelectionWidth,   ref _vis.SegSelectionWidth);
            TryResolveFloat(k_OverlayOpacity,      VisualProperty.OverlayOpacity,      ref _vis.OverlayOpacity);
            TryResolveFloat(k_BarSpacingRatio,     VisualProperty.BarSpacingRatio,     ref _vis.BarSpacingRatio);
            TryResolveFloat(k_MinBarWidth,         VisualProperty.MinBarWidthPx,       ref _vis.MinBarWidthPx);
            TryResolveFloat(k_GridLineWidth,       VisualProperty.GridLineWidth,       ref _vis.GridLineWidth);
            TryResolveFloat(k_AxisLineWidth,       VisualProperty.AxisLineWidth,       ref _vis.AxisLineWidth);
            TryResolveFloat(k_PaddingLeft,         VisualProperty.PaddingLeft,         ref _vis.PaddingLeft);
            TryResolveFloat(k_PaddingRight,        VisualProperty.PaddingRight,        ref _vis.PaddingRight);
            TryResolveFloat(k_PaddingTop,          VisualProperty.PaddingTop,          ref _vis.PaddingTop);
            TryResolveFloat(k_PaddingBottom,       VisualProperty.PaddingBottom,       ref _vis.PaddingBottom);
            TryResolveFloat(k_LabelHeight,         VisualProperty.LabelHeight,         ref _vis.LabelHeight);
            TryResolveFloat(k_XLabelWidth,         VisualProperty.XLabelWidth,         ref _vis.XLabelWidth);
            TryResolveFloat(k_XLabelOffsetY,       VisualProperty.XLabelOffsetY,       ref _vis.XLabelOffsetY);
            TryResolveFloat(k_YLabelGap,           VisualProperty.YLabelGap,           ref _vis.YLabelGap);
            TryResolveFloat(k_DimOpacity,          VisualProperty.DimOpacity,          ref _vis.DimOpacity);
            TryResolveFloat(k_TagHighlightOutlineWidth, VisualProperty.TagHighlightOutlineWidth, ref _vis.TagHighlightOutlineWidth);

            RebuildLabelPool();
            MarkDirtyRepaint();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Input source and handler registration
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sets the input source that translates raw input into
        /// <see cref="BarGraphEventBus"/> calls.  Replaces any existing source.
        /// If the source implements <see cref="IBarGraphInputSource"/>, the
        /// internal event bus is injected automatically — callers never need
        /// to access it directly.
        /// </summary>
        public void SetInputSource(Manipulator inputSource)
        {
            if (_inputSource != null)
                this.RemoveManipulator(_inputSource);

            _inputSource = inputSource;

            if (_inputSource != null)
            {
                if (_inputSource is IBarGraphInputSource src)
                    src.Initialize(_eventBus);

                this.AddManipulator(_inputSource);
            }
        }

        /// <summary>
        /// Adds a behaviour handler and registers it against the internal event bus.
        /// If a handler of the same type is already registered it is replaced.
        /// </summary>
        public void AddHandler(IBarGraphHandler handler)
        {
            for (int i = 0; i < _handlers.Count; i++)
            {
                if (_handlers[i].GetType() == handler.GetType())
                {
                    _handlers[i].Unregister(_eventBus);
                    _handlers[i] = handler;
                    handler.Register(_eventBus, this);
                    return;
                }
            }

            _handlers.Add(handler);
            handler.Register(_eventBus, this);
        }

        /// <summary>Removes and unregisters a handler by type.</summary>
        public void RemoveHandler<T>() where T : IBarGraphHandler
        {
            for (int i = 0; i < _handlers.Count; i++)
            {
                if (_handlers[i] is T)
                {
                    _handlers[i].Unregister(_eventBus);
                    _handlers.RemoveAt(i);
                    return;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public properties
        // ─────────────────────────────────────────────────────────────────────

        /// <summary> Read-only access to the chart model bar count </summary>
        public int BarCount => _model.BarCount;

        /// <summary>Returns the number of segments in the bar at <paramref name="barDataIndex"/>, or 0 if out of range.</summary>
        public int GetSegmentCount(int barDataIndex)
            => barDataIndex >= 0 && barDataIndex < _model.BarCount
                ? _model.Bars[barDataIndex].SegmentCount : 0;
        
        /// <summary>Read-only access to the live view state (for external inspection).</summary>
        public ChartViewState ViewState => _viewState;

        /// <summary>Current behavioural settings. Use <see cref="UpdateSettings"/> to apply changes.</summary>
        public BarGraphSettings Settings => _settings;

        /// <summary>Read-only access to the bar array. Do not write past BarCount-1.</summary>
        public BarEntry[] Bars => _model.Bars;

        /// <summary>Read-only access to the segment array. Do not write past SegmentCount-1.</summary>
        public BarSegment[] Segments => _model.Segments;

        /// <summary>Number of segments in the current dataset.</summary>
        public int SegmentCount => _model.SegmentCount;

        /// <summary>Retrieve a registered handler by type, or null if not found.</summary>
        public T GetHandler<T>() where T : class, IBarGraphHandler
        {
            for (int i = 0; i < _handlers.Count; i++)
                if (_handlers[i] is T h) return h;
            return null;
        }

        /// <summary>
        /// Pluggable Y-axis label formatter.
        /// Signature: <c>float value → string label</c>.
        /// Defaults to SI-suffix formatting (1.2K, 3.4M, etc.).
        /// </summary>
        public Func<float, string> FormatYLabel
        {
            get => _yFormatter;
            set { _yFormatter = value ?? DefaultYFormatter; MarkDirtyRepaint(); }
        }

        /// <summary>
        /// Pluggable X-axis label formatter.
        /// Signature: <c>int barDataIndex → string label</c>.
        /// Defaults to the bar's own <see cref="BarEntry.Label"/> or the index.
        /// </summary>
        public Func<int, string> FormatXLabel
        {
            get => _xFormatter;
            set { _xFormatter = value ?? DefaultXFormatter; MarkDirtyRepaint(); }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public API – data
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Full stacked-bar data.  Both arrays are copied into pre-allocated
        /// internal storage – no heap allocation after warm-up.
        /// </summary>
        public void SetData(BarEntry[] bars, int barCount, BarSegment[] segments, int segCount,
                            BarGraphSettings settings = null)
        {
            if (settings != null) _settings = settings;
            _model.SetData(bars, barCount, segments, segCount);
            // DataChanged fires → OnDataChanged()
        }

        /// <summary>Convenience: flat float values, single colour per bar.</summary>
        public void SetData(IList<float> values, Color? barColor = null,
                            BarGraphSettings settings = null)
        {
            if (settings != null) _settings = settings;
            Color32 c = barColor.HasValue ? (Color32)barColor.Value : default;
            _model.SetData(values, c);
        }

        /// <summary>Convenience: single-segment BarEntry list.</summary>
        public void SetData(IList<BarEntry> bars, BarGraphSettings settings = null)
        {
            if (settings != null) _settings = settings;
            _model.SetData(bars);
        }

        /// <summary>Set the secondary comparison overlay dataset (flat floats).</summary>
        public void SetOverlay(IList<float> values, Color? overlayColor = null)
        {
            Color32 c = overlayColor.HasValue ? (Color32)overlayColor.Value : (Color32)_vis.OverlayTint;
            _model.SetOverlay(values, c);
        }

        /// <summary>Remove the overlay series.</summary>
        public void ClearOverlay() => _model.ClearOverlay();

        /// <summary>Append a single bar without rebuilding the dataset.</summary>
        public void AppendBar(float value, Color? color = null, string label = null)
        {
            Color32 c = color.HasValue ? (Color32)color.Value : default;
            _model.AppendBar(value, c, label);
        }

        /// <summary>Remove all bars and segments.</summary>
        public void ClearData() => _model.Clear();

        // ─────────────────────────────────────────────────────────────────────
        //  Public API – settings / sort / view
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Apply new behavioural settings without touching data.</summary>
        public void UpdateSettings(BarGraphSettings s)
        {
            _settings = s ?? throw new ArgumentNullException(nameof(s));
            RecalcBounds();
            RebuildLabelPool();
            MarkDirtyRepaint();
        }

        /// <summary>
        /// Override the bar spacing ratio programmatically.
        /// 0 = contiguous, 0.12 = 12% gap (USS default). Takes precedence over USS.
        /// </summary>
        public void SetBarSpacingRatio(float ratio)
        {
            _vis.BarSpacingRatio = Mathf.Clamp01(ratio);
            _visOverrides |= (uint)VisualProperty.BarSpacingRatio;
            MarkDirtyRepaint();
        }

        /// <summary>Change the display sort order.</summary>
        public void SetSortMode(SortMode mode, bool descending = true)
        {
            _viewState.SortMode       = mode;
            _viewState.SortDescending = descending;
            _viewState.SortDirty      = true;
            NotifyViewChanged();
            MarkDirtyRepaint();
        }

        /// <summary>Reset zoom, pan, and sort to defaults.</summary>
        public void ResetView()
        {
            _viewState.ResetZoomPan();
            NotifyViewChanged();
            MarkDirtyRepaint();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Domain-reload snapshot (view state only — no data arrays)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Captures the current view state (zoom, pan, sort, selection) into a
        /// lightweight serializable struct.  Call from <c>OnDisable</c> before
        /// domain reload and hold the result in a <c>[SerializeField]</c> field.
        /// </summary>
        public BarGraphViewSnapshot CreateViewSnapshot()
        {
            int[] selected;
            if (_viewState.SelectedBars.Count > 0)
            {
                selected = new int[_viewState.SelectedBars.Count];
                _viewState.SelectedBars.CopyTo(selected);
            }
            else
            {
                selected = Array.Empty<int>();
            }

            return new BarGraphViewSnapshot
            {
                ZoomX           = _viewState.ZoomX,
                ZoomY           = _viewState.ZoomY,
                PanX            = _viewState.PanX,
                PanY            = _viewState.PanY,
                SortMode        = _viewState.SortMode,
                SortDescending  = _viewState.SortDescending,
                FocusedBarIndex      = _viewState.FocusedBarIndex,
                SelectedBars         = selected,
                SelectedSegmentBar   = _viewState.SelectedSegmentBar,
                SelectedSegmentIndex = _viewState.SelectedSegmentIndex,
                HighlightedTag       = _viewState.HighlightedTag,
                IsValid              = true,
            };
        }

        /// <summary>
        /// Restores a previously captured view snapshot.  Call AFTER data has
        /// been loaded (via <see cref="SetData"/>) so that sort maps and pan
        /// clamping work against the correct bar count.
        /// </summary>
        public void RestoreViewSnapshot(BarGraphViewSnapshot snap)
        {
            if (!snap.IsValid) return;

            _viewState.ZoomX          = Mathf.Clamp(snap.ZoomX, _viewState.MinZoomX, _viewState.MaxZoomX);
            _viewState.ZoomY          = Mathf.Clamp(snap.ZoomY, _viewState.MinZoomY, _viewState.MaxZoomY);
            _viewState.PanX           = snap.PanX;
            _viewState.PanY           = snap.PanY;
            _viewState.SortMode       = snap.SortMode;
            _viewState.SortDescending = snap.SortDescending;
            _viewState.SortDirty      = true;

            // Clamp index-based state against actual bar count
            int barCount = _model.BarCount;
            _viewState.FocusedBarIndex =
                (snap.FocusedBarIndex >= 0 && snap.FocusedBarIndex < barCount)
                    ? snap.FocusedBarIndex : -1;

            // Direct mutation for bulk restore — avoids N SelectionChanged events.
            // Single FireSelectionChanged() call below covers the state change.
            _viewState.SelectedBars.Clear();
            _viewState.SelectedSegmentBar   = (snap.SelectedSegmentBar >= 0 && snap.SelectedSegmentBar < barCount)
                ? snap.SelectedSegmentBar : -1;
            _viewState.SelectedSegmentIndex = (_viewState.SelectedSegmentBar >= 0)
                ? snap.SelectedSegmentIndex : -1;
            _viewState.HighlightedTag = snap.HighlightedTag;
            if (snap.SelectedBars != null)
            {
                for (int i = 0; i < snap.SelectedBars.Length; i++)
                {
                    int idx = snap.SelectedBars[i];
                    if (idx >= 0 && idx < barCount)
                        _viewState.SelectedBars.Add(idx);
                }
            }

            ClampViewState();
            NotifyViewChanged();
            FireSelectionChanged();
            MarkDirtyRepaint();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Internal geometry helpers (used by manipulators)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Bar stride (slot width) in pixels at current zoom, including spacing.</summary>
        internal float GetBarStride()
        {
            float plotW = GetPlotWidth();
            int   total = _model.BarCount;
            if (total <= 0) return plotW;
            // At ZoomX=1 all bars fit in plotW; each bar takes plotW/total
            return plotW / total * _viewState.ZoomX;
        }

        /// <summary>Bar stride at zoom = 1 (used by ZoomManipulator for anchor maths).</summary>
        internal float GetBarStrideBase()
        {
            float plotW = GetPlotWidth();
            int   total = _model.BarCount;
            return total > 0 ? plotW / total : plotW;
        }

        internal float GetPlotWidth()  =>
            Mathf.Max(1f, contentRect.width  - _vis.PaddingLeft - _vis.PaddingRight);

        internal float GetPlotHeight() =>
            Mathf.Max(1f, contentRect.height - _vis.PaddingTop  - _vis.PaddingBottom);

        // ─────────────────────────────────────────────────────────────────────
        //  Internal state mutators (called by manipulators; each dirty-repaints)
        // ─────────────────────────────────────────────────────────────────────

        internal void InternalSetHover(int dataIndex)
        {
            InternalSetHover(dataIndex, new Vector2(-1f, -1f));
        }

        /// <summary>
        /// Sets the hovered bar AND resolves the hovered segment from the
        /// cursor's Y position.  Manipulators that have the local position
        /// should prefer this overload for segment-level hover feedback.
        /// </summary>
        internal void InternalSetHover(int dataIndex, Vector2 localPos)
        {
            bool barChanged = _viewState.HoveredBarIndex != dataIndex;
            if (barChanged)
            {
                _viewState.HoveredBarIndex = dataIndex;
                float val = (dataIndex >= 0 && dataIndex < _model.BarCount)
                    ? _model.Bars[dataIndex].TotalValue : 0f;
                HoverChanged?.Invoke(new HoverChangedEventArgs(dataIndex, val));
            }

            // Resolve segment under cursor
            var segHit = HitTestSegment(localPos, dataIndex);

            bool segChanged = _viewState.HoveredSegmentBar   != segHit.BarDataIndex
                           || _viewState.HoveredSegmentIndex != segHit.SegmentIndex;

            _viewState.HoveredSegmentBar   = segHit.BarDataIndex;
            _viewState.HoveredSegmentIndex = segHit.SegmentIndex;

            if (segChanged)
            {
                SegmentHoverChanged?.Invoke(new SegmentEventArgs
                {
                    BarDataIndex    = segHit.BarDataIndex,
                    BarDisplayIndex = segHit.BarDataIndex >= 0 && segHit.BarDataIndex < _viewState.DataToDisplay.Length
                                       ? _viewState.DataToDisplay[segHit.BarDataIndex] : -1,
                    SegmentIndex    = segHit.SegmentIndex,
                    Tag             = segHit.Tag,
                    Value           = segHit.Value,
                    Color           = segHit.Color,
                    LocalPosition   = localPos,
                });
            }

            if (barChanged || segChanged)
                MarkDirtyRepaint();
        }

        /// <summary>
        /// Converts a screen-space pixel delta into data-space pan offsets.
        /// Absorbs the Y-axis inversion (screen Y-down vs chart Y-up) so that
        /// callers can apply both axes with the same sign: <c>startPan + delta</c>.
        /// </summary>
        internal Vector2 ScreenDeltaToPanDelta(Vector2 screenDelta)
        {
            float barStride = GetBarStride();
            float plotH     = GetPlotHeight();
            return new Vector2(
                -screenDelta.x / Mathf.Max(1f, barStride),
                 screenDelta.y / Mathf.Max(1f, plotH)
            );
        }

        internal void InternalSetPan(float panX, float panY)
        {
            _viewState.PanX = panX;
            _viewState.PanY = panY;
            ClampViewState();
            NotifyViewChanged();
            MarkDirtyRepaint();
        }

        internal void InternalSetZoom(float zoomX, float zoomY, float panX, float panY)
        {
            _viewState.ZoomX = Mathf.Clamp(zoomX, _viewState.MinZoomX, _viewState.MaxZoomX);
            _viewState.ZoomY = Mathf.Clamp(zoomY, _viewState.MinZoomY, _viewState.MaxZoomY);
            _viewState.PanX  = panX;
            _viewState.PanY  = panY;
            ClampViewState();
            NotifyViewChanged();
            MarkDirtyRepaint();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public selection API
        //  Consumers call these to mutate selection state.
        //  Each fires SelectionChanged.  Handlers decide WHEN to call them.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Adds <paramref name="dataIndex"/> to the selection set.
        /// When <paramref name="additive"/> is false, clears existing selection first.
        /// Clears segment selection. Fires <see cref="SelectionChanged"/>.
        /// </summary>
        public void SelectBar(int dataIndex, bool additive = false)
        {
            if (!_settings.EnableSelection) return;
            if (!additive) _viewState.SelectedBars.Clear();

            _viewState.SelectedSegmentBar   = -1;
            _viewState.SelectedSegmentIndex = -1;

            if (dataIndex >= 0)
            {
                _viewState.SelectedBars.Add(dataIndex);
                _viewState.FocusedBarIndex = dataIndex;
            }
            FireSelectionChanged();
            MarkDirtyRepaint();
        }

        /// <summary>
        /// Removes <paramref name="dataIndex"/> from the selection set.
        /// No-op if <paramref name="dataIndex"/> is not currently selected.
        /// Fires <see cref="SelectionChanged"/>.
        /// </summary>
        public void DeselectBar(int dataIndex)
        {
            _viewState.SelectedBars.Remove(dataIndex);
            FireSelectionChanged();
            MarkDirtyRepaint();
        }

        /// <summary>
        /// If <paramref name="dataIndex"/> is not selected, adds it; if already selected, removes it.
        /// When <paramref name="additive"/> is false, clears existing selection before toggling.
        /// Clears segment selection. Fires <see cref="SelectionChanged"/>.
        /// </summary>
        public void ToggleBar(int dataIndex, bool additive = false)
            => InternalSelectBar(dataIndex, additive);

        /// <summary>
        /// Selects a single segment. Clears bar selection.
        /// Fires <see cref="SelectionChanged"/>.
        /// </summary>
        public void SelectSegment(int barDataIndex, int segmentIndex)
            => InternalSelectSegment(barDataIndex, segmentIndex);

        /// <summary>
        /// Clears all bar and segment selection.
        /// Fires <see cref="SelectionChanged"/>.
        /// </summary>
        public void ClearSelection()
        {
            _viewState.SelectedBars.Clear();
            _viewState.SelectedSegmentBar   = -1;
            _viewState.SelectedSegmentIndex = -1;
            _viewState.FocusedBarIndex      = -1;
            FireSelectionChanged();
            MarkDirtyRepaint();
        }

        /// <summary>
        /// Highlights all segments across all bars whose <see cref="BarSegment.Tag"/>
        /// matches the given value.  Set to -1 to clear.  Opt-in: default is -1 (no highlight).
        /// </summary>
        public void HighlightTag(int tag)
        {
            _viewState.HighlightedTag = tag;
            MarkDirtyRepaint();
        }

        /// <summary>Clears the tag highlight. Equivalent to <c>HighlightTag(-1)</c>.</summary>
        public void ClearTagHighlight()
        {
            _viewState.HighlightedTag = -1;
            MarkDirtyRepaint();
        }

        /// <summary>Currently highlighted tag, or -1 if none.</summary>
        public int HighlightedTag => _viewState.HighlightedTag;

        /// <summary>
        /// Clears segment selection without affecting bar selection (dimming).
        /// </summary>
        public void ClearSegmentSelection()
        {
            _viewState.SelectedSegmentBar   = -1;
            _viewState.SelectedSegmentIndex = -1;
            MarkDirtyRepaint();
        }

        /// <summary>
        /// Selects all bars. Clears segment selection.
        /// Fires <see cref="SelectionChanged"/>.
        /// </summary>
        public void SelectAll()
        {
            _viewState.SelectedBars.Clear();
            _viewState.SelectedSegmentBar   = -1;
            _viewState.SelectedSegmentIndex = -1;
            for (int i = 0; i < _model.BarCount; i++)
                _viewState.SelectedBars.Add(i);
            FireSelectionChanged();
            MarkDirtyRepaint();
        }

        /// <summary>Show the drag-selection rectangle during a drag gesture.</summary>
        public void UpdateDragRect(Rect rect)
            => InternalUpdateDragRect(rect);

        /// <summary>
        /// Commit a drag-selection: selects all bars intersecting <paramref name="rect"/>.
        /// Fires <see cref="SelectionChanged"/>.
        /// </summary>
        public void CommitDragSelection(Rect rect, bool additive = false)
            => InternalCommitDragSelection(rect, additive);

        /// <summary>Cancel an in-progress drag without committing.</summary>
        public void CancelDrag()
            => InternalCancelDrag();

        /// <summary>Scrolls PanX so <paramref name="dataIndex"/> is within the visible window.</summary>
        public void EnsureBarVisible(int dataIndex)
            => EnsureBarVisible_Internal(dataIndex);

        internal void InternalSelectBar(int dataIndex, bool additive)
        {
            if (!_settings.EnableSelection) return;
            if (!additive) _viewState.SelectedBars.Clear();

            // Bar selection clears segment selection
            _viewState.SelectedSegmentBar   = -1;
            _viewState.SelectedSegmentIndex = -1;

            if (dataIndex >= 0)
            {
                if (!_viewState.SelectedBars.Add(dataIndex))
                    _viewState.SelectedBars.Remove(dataIndex); // toggle on re-click
                _viewState.FocusedBarIndex = dataIndex;
            }
            FireSelectionChanged();
            MarkDirtyRepaint();
        }

        /// <summary>
        /// Selects a single segment within a bar.
        /// Clears bar selection unless <see cref="BarGraphSettings.ClearBarsOnSegmentSelect"/> is false.
        /// Fires <see cref="SelectionChanged"/>.
        /// </summary>
        internal void InternalSelectSegment(int barDataIndex, int segmentIndex)
        {
            if (!_settings.EnableSelection) return;

            if (_settings.ClearBarsOnSegmentSelect)
                _viewState.SelectedBars.Clear();
            _viewState.SelectedSegmentBar   = barDataIndex;
            _viewState.SelectedSegmentIndex = segmentIndex;
            FireSelectionChanged();
            MarkDirtyRepaint();
        }

        internal void InternalUpdateDragRect(Rect rect)
        {
            _viewState.DragRect          = rect;
            _viewState.IsDragSelecting   = true;
            MarkDirtyRepaint();
        }

        internal void InternalCommitDragSelection(Rect rect, bool additive)
        {
            _viewState.IsDragSelecting = false;
            _viewState.DragRect        = Rect.zero;

            if (!additive) _viewState.SelectedBars.Clear();

            // Drag selection clears segment selection
            _viewState.SelectedSegmentBar   = -1;
            _viewState.SelectedSegmentIndex = -1;

            // Select all bars whose rendered rect intersects the drag rect
            CollectBarsInRect(rect, _viewState.SelectedBars);
            FireSelectionChanged();
            MarkDirtyRepaint();
        }

        internal void InternalCancelDrag()
        {
            _viewState.IsDragSelecting = false;
            _viewState.DragRect        = Rect.zero;
            MarkDirtyRepaint();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Hit-testing (O(1) for uniformly-spaced bars)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the DATA index of the bar under <paramref name="localPos"/>,
        /// or -1 if none.
        /// </summary>
        public int HitTestBar(Vector2 localPos)
        {
            if (_model.BarCount == 0) return -1;

            float plotX  = _vis.PaddingLeft;
            float plotY2 = contentRect.height - _vis.PaddingBottom;

            // Must be inside the plot area
            if (localPos.x < plotX || localPos.y > plotY2) return -1;

            float stride  = GetBarStride();   // pixels per slot at current zoom
            float gapPx   = stride * _vis.BarSpacingRatio;
            float barW    = Mathf.Max(_vis.MinBarWidthPx, stride - gapPx);

            float relX    = localPos.x - plotX + _viewState.PanX * stride;
            if (relX < 0f) return -1;

            int displayIdx = (int)(relX / stride);
            if (displayIdx < 0 || displayIdx >= _model.BarCount) return -1;

            // Confirm cursor is on the bar, not the gap
            SnapBarX(displayIdx, plotX, stride, barW, out float barStartX, out float snapW);
            if (localPos.x < barStartX || localPos.x > barStartX + snapW) return -1;

            // Resolve display → data
            EnsureSortMap();
            return displayIdx < _viewState.DisplayToData.Length
                ? _viewState.DisplayToData[displayIdx] : displayIdx;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Segment hit-testing
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves which segment the cursor is over within a bar.
        /// Walks the bar's segment stack using the same Y math as
        /// <see cref="DrawDirectBars"/>.  Returns <see cref="SegmentHitResult.Miss"/>
        /// when in LOD mode (stride &lt; 1px), when the bar is missed, or when
        /// the cursor is in a gap between segments.
        /// <para>O(segments_per_bar) — only called on mouse events, not per-frame.</para>
        /// </summary>
        public SegmentHitResult HitTestSegment(Vector2 localPos, int barDataIndex = -2)
        {
            if (_model.BarCount == 0) return SegmentHitResult.Miss;

            // Resolve bar if not provided (-2 = not supplied, -1 = known miss)
            if (barDataIndex == -2)
                barDataIndex = HitTestBar(localPos);
            if (barDataIndex < 0 || barDataIndex >= _model.BarCount)
                return SegmentHitResult.Miss;

            // No segment interaction in LOD mode
            float stride = GetBarStride();
            if (stride < 1f) return SegmentHitResult.Miss;

            float plotY2 = contentRect.height - _vis.PaddingBottom;
            float plotH  = GetPlotHeight();
            float yScale = plotH / _effectiveMaxY * _viewState.ZoomY;
            float yOffset = _viewState.PanY * plotH;

            ref readonly BarEntry bar = ref _model.Bars[barDataIndex];
            float yBottom = plotY2 + yOffset;

            for (int s = 0; s < bar.SegmentCount; s++)
            {
                ref readonly BarSegment seg = ref _model.Segments[bar.SegmentStart + s];
                float segH = seg.Value * yScale;

                // Skip sub-pixel segments (they're LOD-merged in rendering,
                // so clicking them would be misleading)
                if (segH < 1f) { yBottom -= segH; continue; }

                float yTop = yBottom - segH;

                if (localPos.y >= yTop && localPos.y <= yBottom)
                {
                    return new SegmentHitResult
                    {
                        BarDataIndex  = barDataIndex,
                        SegmentIndex  = s,
                        Tag           = seg.Tag,
                        Value         = seg.Value,
                        Color         = seg.Color,
                    };
                }
                yBottom = yTop;
            }

            return SegmentHitResult.Miss;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Keyboard navigation
        // ─────────────────────────────────────────────────────────────────────

        // OnKeyDown, MoveFocus, SetFocus, ActivateFocusedBar, SelectAll
        // extracted to BarGraphKeyboardSelectionHandler

        /// <summary>Scrolls PanX so <paramref name="dataIdx"/> is within the visible window.</summary>
        private void EnsureBarVisible_Internal(int dataIdx)
        {
            EnsureSortMap();
            int displayIdx = dataIdx < _viewState.DataToDisplay.Length
                ? _viewState.DataToDisplay[dataIdx] : dataIdx;

            float plotW       = GetPlotWidth();
            float stride      = GetBarStrideBase();
            float visibleBars = plotW / (stride * _viewState.ZoomX);
            float barLeft     = displayIdx;
            float barRight    = displayIdx + 1f;

            if (barLeft < _viewState.PanX)
                _viewState.PanX = barLeft;
            else if (barRight > _viewState.PanX + visibleBars)
                _viewState.PanX = barRight - visibleBars;

            ClampViewState();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Core rendering  (generateVisualContent)
        // ─────────────────────────────────────────────────────────────────────

        private void OnGenerateVisualContent(MeshGenerationContext mgc)
        {
            Rect cr = contentRect;
            if (cr.width < 2f || cr.height < 2f) return;

            // Lazy sort rebuild
            EnsureSortMap();

            Painter2D p = mgc.painter2D;

            // 1 – Background
            FillRect(p, _vis.BackgroundColor, 0, 0, cr.width, cr.height);

            // 2 – Plot-area bounds
            float pL    = _vis.PaddingLeft;
            float pT    = _vis.PaddingTop;
            float plotX  = pL;
            float plotY  = pT;
            float plotW  = cr.width  - pL - _vis.PaddingRight;
            float plotH  = cr.height - pT - _vis.PaddingBottom;
            float plotX2 = plotX + plotW;
            float plotY2 = plotY + plotH;   // bottom edge (Y increases downward)

            if (plotW < 2f || plotH < 2f) return;

            // 3 – Horizontal grid lines (below bars)
            if (_settings.ShowGrid && _settings.GridLineCount > 0)
                DrawGrid(p, plotX, plotX2, plotY, plotY2, plotH);

            // Compute view-slice once for context + bar rendering
            float ctxStride = 0f, ctxBarW = 0f;
            int   ctxStart = -1, ctxEnd = -1;
            if (_model.BarCount > 0)
                CalcViewSlice(plotW, out ctxStride, out ctxBarW, out ctxStart, out ctxEnd);

            // Build draw context for custom callbacks (stack-allocated, constructed once)
            var plotRect = new Rect(plotX, plotY, plotW, plotH);
            var drawCtx = new BarGraphDrawContext(
                p, mgc, plotRect,
                _viewState.ZoomX, _viewState.ZoomY,
                _viewState.PanX, _viewState.PanY,
                _effectiveMaxY, ctxStride, ctxBarW,
                ctxStart, ctxEnd, _model.BarCount);

            // ── Custom draw hook: before bars ──
            _onDrawBeforeBars?.Invoke(in drawCtx);

            // 4 – Primary bars (stacked, segment-LOD, direct mesh)
            _quadCount = 0;   // Reset quad buffer for this repaint

            if (_model.BarCount > 0)
            {
                int viewCount = ctxEnd - ctxStart;

                // Pre-size quad buffer to avoid repeated Array.Resize during
                // the draw pass.  Direct path: segment merge caps output at
                // ~plotH quads per bar.  LOD path: bounded by pixel count.
                // Overlay may add a similar amount — multiply by 2 if present.
                if (viewCount > 0)
                {
                    int estimatedQuads = ctxStride >= 1f
                        ? viewCount * Mathf.CeilToInt(plotH)
                        : Mathf.CeilToInt(plotW);
                    if (_model.HasOverlay) estimatedQuads *= 2;
                    if (_viewState.HighlightedTag >= 0) estimatedQuads += estimatedQuads / 4;
                    if (_quadBuf.Length < estimatedQuads)
                        _quadBuf = new QuadData[Mathf.Max(_quadBuf.Length * 2, estimatedQuads)];
                }

                if (viewCount > 0)
                {
                    if (ctxStride >= 1f)
                        DrawDirectBars(ctxStart, ctxEnd, plotX, plotY2, ctxStride, ctxBarW,
                                       plotH, _model, false, 1f);
                    else
                        DrawLodBars(ctxStart, ctxEnd, plotX, plotY2, plotW, plotH,
                                    _model, false, 1f);
                }
            }

            // 5 – Overlay bars (same position mapping, tinted alpha)
            // When a tag highlight is active and bars are in direct mode (segments visible),
            // skip the overlay — the segment-level tint provides the same information.
            if (_model.HasOverlay && _model.BarCount > 0)
            {
                int viewCount = ctxEnd - ctxStart;
                bool suppressOverlay = _viewState.HighlightedTag >= 0 && ctxStride >= 1f;

                if (viewCount > 0 && !suppressOverlay)
                {
                    if (ctxStride >= 1f)
                        DrawDirectBars(ctxStart, ctxEnd, plotX, plotY2, ctxStride, ctxBarW,
                                       plotH, _model, true, _vis.OverlayOpacity);
                    else
                        DrawLodBars(ctxStart, ctxEnd, plotX, plotY2, plotW, plotH,
                                    _model, true, _vis.OverlayOpacity);
                }
            }

            // Flush all accumulated bar quads to the GPU via direct mesh allocation.
            // This bypasses Painter2D entirely — no tessellation, no vertex ceiling.
            if (_quadCount > 0)
                FlushQuads(mgc);

            // ── Custom draw hook: after bars ──
            _onDrawAfterBars?.Invoke(in drawCtx);

            // 6 – Selection & hover highlights
            DrawHighlights(p, plotX, plotY2, plotH);

            // 7 – Drag-select rectangle
            if (_viewState.IsDragSelecting && _viewState.DragRect.width > 1f)
                DrawDragRect(p, _viewState.DragRect);

            // 7b – Padding overdraw: re-fill the four padding strips with the
            //      background colour.  The bar clamp prevents bars from drawing
            //      above plotY (= PaddingTop), but the top Y-label is centred ON
            //      plotY so its lower half sits inside the plot area.  Overpainting
            //      the strips creates a clean frame that masks any bar tip that
            //      touches a boundary and keeps label backgrounds opaque.
            FillRect(p, _vis.BackgroundColor, 0,      0,      cr.width,           pT);
            FillRect(p, _vis.BackgroundColor, 0,      plotY2, cr.width,           cr.height - plotY2);
            FillRect(p, _vis.BackgroundColor, 0,      pT,     pL,                 plotH);
            FillRect(p, _vis.BackgroundColor, plotX2, pT,     cr.width - plotX2,  plotH);

            // 8 – Axes (on top of bars and overdraw)
            if (_settings.ShowAxes)
                DrawAxes(p, plotX, plotX2, plotY, plotY2);

            // ── Custom draw hook: after all chrome ──
            _onDrawAfterChrome?.Invoke(in drawCtx);

            // 9 – Cache label layout params for the deferred FlushLabelPositions
            //     scheduler (must NOT modify VisualElement styles here).
            _lblPlotX    = plotX;
            _lblPlotW    = plotW;
            _lblPlotH    = plotH;
            _lblPlotY2   = plotY2;
            _labelsDirty = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Grid
        // ─────────────────────────────────────────────────────────────────────

        private void DrawGrid(Painter2D p,
            float plotX, float plotX2, float plotY, float plotY2, float plotH)
        {
            p.strokeColor = _vis.GridLineColor;
            p.lineWidth   = _vis.GridLineWidth;
            int lines = _settings.GridLineCount;
            for (int i = 0; i <= lines; i++)
            {
                float t = (float)i / lines;
                float y = plotY2 - t * plotH;
                p.BeginPath();
                p.MoveTo(new Vector2(plotX,  y));
                p.LineTo(new Vector2(plotX2, y));
                p.Stroke();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Direct bar rendering  (slot ≥ 1 px; supports stacked segments)
        // ─────────────────────────────────────────────────────────────────────

        private void DrawDirectBars(
            int startDisp, int endDisp,
            float plotX, float plotY2,
            float stride, float barW, float plotH,
            ChartDataModel model, bool useOverlay, float alpha)
        {
            BarEntry[]   bars     = useOverlay ? model.OverlayBars     : model.Bars;
            BarSegment[] segments = useOverlay ? model.OverlaySegments : model.Segments;
            int          barCount = useOverlay ? model.OverlayBarCount  : model.BarCount;

            // Effective Y scale: _effectiveMaxY applies Y zoom and pan
            float yScale = plotH / _effectiveMaxY * _viewState.ZoomY;
            // PanY shifts the bottom of the Y range
            float yOffset = _viewState.PanY * plotH;
            float plotTop = plotY2 - plotH;   // top edge of plot area

            bool hasSel  = _viewState.SelectedBars.Count > 0;
            float dimAlpha = _vis.DimOpacity;

            // Tag highlight — cached outside loop for zero per-iteration field access
            int highlightTag = useOverlay ? -1 : _viewState.HighlightedTag;
            Color32 tagTint  = (Color32)_vis.TagHighlightTint;
            bool hasTagTint  = highlightTag >= 0 && tagTint.a > 0;
            var tagFilter    = hasTagTint ? _tagHighlightFilter : null;

            // Per-bar visual provider — only for primary bars (not overlays).
            // Cached ref avoids field access per iteration.
            var barVisProv = useOverlay ? null : _barVisualProvider;

            for (int dispIdx = startDisp; dispIdx < endDisp; dispIdx++)
            {
                int dataIdx = _viewState.DisplayToData[dispIdx];
                if (dataIdx >= barCount) continue;

                ref readonly BarEntry bar = ref bars[dataIdx];
                SnapBarX(dispIdx, plotX, stride, barW, out float x, out float bw);
                float yBottom = plotY2 + yOffset;   // +yOffset shifts range up when panned

                float barAlpha = alpha;
                if (hasSel && !_viewState.SelectedBars.Contains(dataIdx))
                    barAlpha *= dimAlpha;
                bool barTagTint = hasTagTint && (tagFilter == null || tagFilter.Invoke(dataIdx));

                // Query per-bar visual provider (zero-alloc: Nullable<struct> on stack)
                Color32 colorOverride = default;
                bool    hasColorOvr   = false;
                if (barVisProv != null)
                {
                    BarVisualOverride? ovr = barVisProv.Invoke(dataIdx);
                    if (ovr.HasValue)
                    {
                        if (ovr.Value.Alpha.HasValue)
                            barAlpha *= ovr.Value.Alpha.Value;
                        if (ovr.Value.Color.HasValue)
                        {
                            colorOverride = ovr.Value.Color.Value;
                            hasColorOvr   = true;
                        }
                    }
                }

                // ── Segment-level LOD merge state (zero allocation) ─────────
                // When consecutive segments are each < 1 px tall, accumulate
                // their heights and emit one merged rect using the dominant
                // colour (largest-value segment wins — matches DrawLodBars
                // strategy).  This caps output to ~plotH rects per bar
                // regardless of segment count.
                float   mergeH      = 0f;
                Color32 mergeColor  = default;
                float   mergeDomVal = 0f;
                int     mergeDomTag = -1;

                for (int s = 0; s < bar.SegmentCount; s++)
                {
                    ref readonly BarSegment seg = ref segments[bar.SegmentStart + s];
                    // Do NOT cap segH to plotH here. When ZoomY > 1 bars are taller
                    // than the plot area, and the cap would prevent them from ever
                    // reaching the top edge during Y-pan. Real clipping is done
                    // correctly by drawTop/drawH below.
                    float segH = seg.Value * yScale;

                    Color32 segColor = hasColorOvr ? colorOverride : seg.Color;

                    if (segH < 1f)
                    {
                        // Sub-pixel segment → accumulate into merge buffer.
                        Color32 c = ResolveSegmentColor(segColor, barAlpha);
                        if (seg.Value > mergeDomVal)
                        {
                            mergeDomVal = seg.Value;
                            mergeColor  = c;
                            mergeDomTag = seg.Tag;
                        }
                        mergeH += segH;

                        // Flush the merge buffer when accumulated height reaches 1 px.
                        if (mergeH >= 1f)
                        {
                            float mYTop = yBottom - mergeH;
                            if (mYTop < plotY2 && yBottom > plotTop)
                            {
                                float drawTop = Mathf.Max(mYTop, plotTop);
                                float drawH   = Mathf.Min(yBottom, plotY2) - drawTop;
                                if (drawH >= 0.5f)
                                {
                                    AddQuad(mergeColor, x, drawTop, bw, drawH);
                                    if (barTagTint && mergeDomTag == highlightTag)
                                        AddQuad(tagTint, x, drawTop, bw, drawH);
                                }
                            }
                            yBottom     = mYTop;
                            mergeH      = 0f;
                            mergeDomVal = 0f;
                            mergeColor  = default;
                            mergeDomTag = -1;

                            if (yBottom <= plotTop) break;
                        }
                        continue;
                    }

                    // ≥ 1 px segment: flush any pending merge buffer first.
                    if (mergeH > 0f)
                    {
                        float mYTop = yBottom - mergeH;
                        if (mYTop < plotY2 && yBottom > plotTop)
                        {
                            float drawTop = Mathf.Max(mYTop, plotTop);
                            float drawH   = Mathf.Min(yBottom, plotY2) - drawTop;
                            if (drawH >= 0.5f)
                            {
                                AddQuad(mergeColor, x, drawTop, bw, drawH);
                                if (barTagTint && mergeDomTag == highlightTag)
                                    AddQuad(tagTint, x, drawTop, bw, drawH);
                            }
                        }
                        yBottom     = mYTop;
                        mergeH      = 0f;
                        mergeDomVal = 0f;
                        mergeColor  = default;
                        mergeDomTag = -1;

                        if (yBottom <= plotTop) break;
                    }

                    // Emit this segment as its own rect (existing logic).
                    float yTop = yBottom - segH;
                    if (yTop  >= plotY2)  { yBottom = yTop; continue; }   // below visible
                    if (yBottom <= plotTop) break;                         // above visible

                    float sDrawTop = Mathf.Max(yTop, plotTop);
                    float sDrawH   = Mathf.Min(yBottom, plotY2) - sDrawTop;
                    if (sDrawH < 0.5f) { yBottom = yTop; continue; }

                    Color32 sc = ResolveSegmentColor(segColor, barAlpha);
                    AddQuad(sc, x, sDrawTop, bw, sDrawH);
                    if (barTagTint && seg.Tag == highlightTag)
                        AddQuad(tagTint, x, sDrawTop, bw, sDrawH);
                    yBottom = yTop;
                }

                // Flush any remaining merge buffer after the segment loop.
                if (mergeH > 0f)
                {
                    float mYTop = yBottom - mergeH;
                    if (mYTop < plotY2 && yBottom > plotTop)
                    {
                        float drawTop = Mathf.Max(mYTop, plotTop);
                        float drawH   = Mathf.Min(yBottom, plotY2) - drawTop;
                        if (drawH >= 0.5f)
                        {
                            AddQuad(mergeColor, x, drawTop, bw, drawH);
                            if (barTagTint && mergeDomTag == highlightTag)
                                AddQuad(tagTint, x, drawTop, bw, drawH);
                        }
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  LOD bar rendering  (slot < 1 px → pixel-column merging)
        // ─────────────────────────────────────────────────────────────────────

        private void DrawLodBars(
            int startDisp, int endDisp,
            float plotX, float plotY2,
            float plotW, float plotH,
            ChartDataModel model, bool useOverlay, float alpha)
        {
            int pixelCount = Mathf.Max(1, Mathf.FloorToInt(plotW));
            int viewCount  = endDisp - startDisp;

            if (_lodBuf.Length < pixelCount)
                _lodBuf = new LodPixel[Mathf.NextPowerOfTwo(pixelCount + 1)];

            for (int px = 0; px < pixelCount; px++) _lodBuf[px] = default;

            BarEntry[]   bars     = useOverlay ? model.OverlayBars     : model.Bars;
            BarSegment[] segments = useOverlay ? model.OverlaySegments : model.Segments;
            int          barCount = useOverlay ? model.OverlayBarCount  : model.BarCount;

            bool hasSel  = _viewState.SelectedBars.Count > 0;
            float dimAlpha = _vis.DimOpacity;

            // Per-bar visual provider — only for primary bars (not overlays).
            var barVisProv = useOverlay ? null : _barVisualProvider;

            for (int i = 0; i < viewCount; i++)
            {
                int dataIdx = _viewState.DisplayToData[startDisp + i];
                if (dataIdx >= barCount) continue;

                ref readonly BarEntry bar = ref bars[dataIdx];
                int px = Mathf.Clamp(
                    Mathf.FloorToInt((float)i / viewCount * pixelCount),
                    0, pixelCount - 1);

                float barAlpha = alpha;
                if (hasSel && !_viewState.SelectedBars.Contains(dataIdx))
                    barAlpha *= dimAlpha;

                // For LOD we just use the first segment colour at total value
                float total = bar.TotalValue;
                if (total > _lodBuf[px].MaxValue)
                {
                    Color32 segColor = bar.SegmentCount > 0
                        ? segments[bar.SegmentStart].Color : default;

                    // Apply per-bar visual provider (only for winning bar)
                    if (barVisProv != null)
                    {
                        BarVisualOverride? ovr = barVisProv.Invoke(dataIdx);
                        if (ovr.HasValue)
                        {
                            if (ovr.Value.Alpha.HasValue)
                                barAlpha *= ovr.Value.Alpha.Value;
                            if (ovr.Value.LodColor.HasValue)
                                segColor = ovr.Value.LodColor.Value;
                            else if (ovr.Value.Color.HasValue)
                                segColor = ovr.Value.Color.Value;
                        }
                    }

                    Color32 c = ResolveSegmentColor(segColor, barAlpha);
                    _lodBuf[px] = new LodPixel { MaxValue = total, Color = c, DataIndex = dataIdx };
                }
            }

            float yScale  = plotH / _effectiveMaxY * _viewState.ZoomY;
            float yOffset = _viewState.PanY * plotH;

            for (int px = 0; px < pixelCount; px++)
            {
                LodPixel lp = _lodBuf[px];
                if (lp.MaxValue <= 0f) continue;

                // Same reasoning as DrawDirectBars: don't cap to plotH here.
                // Use the real uncapped height and clip to the plot boundary below.
                float barH    = lp.MaxValue * yScale;
                float yBottom = plotY2 + yOffset;
                float yTop    = yBottom - barH;

                // Clip to plot area
                float drawTop = Mathf.Max(yTop,    plotY2 - plotH);
                float drawBot = Mathf.Min(yBottom, plotY2);
                float drawH   = drawBot - drawTop;
                if (drawH < 0.5f) continue;

                float x = plotX + px;
                AddQuad(lp.Color, x, drawTop, 1f, drawH);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Selection & hover highlights
        // ─────────────────────────────────────────────────────────────────────

        private void DrawHighlights(Painter2D p, float plotX, float plotY2, float plotH)
        {
            if (_model.BarCount == 0) return;

            float plotW = GetPlotWidth();

            // Compute the visible display-index range once.
            CalcViewSlice(plotW, out float stride, out float barW,
                          out int startDisp, out int endDisp);

            // ── LOD mode (stride < 1 px) ──────────────────────────────────────
            // Per-bar highlights are invisible at this zoom AND catastrophically
            // expensive: iterating a 10 000-element SelectedBars HashSet produces
            // 10 000 sub-paths, which stalls the Painter2D tessellator and freezes
            // the editor.  Draw a single aggregate rect instead (O(1)).
            bool lodMode = stride < 1f;

            if (_viewState.SelectedBars.Count > 0)
            {
                if (lodMode)
                {
                    // LOD mode: iterate pixel columns (O(plotWidth), bounded by screen),
                    // check which display index each column maps to, draw a 1 px
                    // highlight column only if that bar is selected.
                    // This is O(pixelCount) with O(1) HashSet.Contains per column —
                    // never O(selected), so stays fast even with 10 000 bars selected.
                    int pixelCount = Mathf.Max(1, Mathf.FloorToInt(plotW));
                    int viewCount  = endDisp - startDisp;

                    p.fillColor = _vis.SelectionFillColor;
                    p.BeginPath();
                    for (int px = 0; px < pixelCount; px++)
                    {
                        // Inverse of DrawLodBars mapping: px → representative display index.
                        int dispOffset = Mathf.FloorToInt((float)px * viewCount / pixelCount);
                        int dispIdx    = startDisp + Mathf.Clamp(dispOffset, 0, viewCount - 1);
                        int dataIdx    = dispIdx < _viewState.DisplayToData.Length
                            ? _viewState.DisplayToData[dispIdx] : dispIdx;

                        if (!_viewState.SelectedBars.Contains(dataIdx)) continue;

                        float x = plotX + px;
                        PathRect(p, x, plotY2 - plotH, 1f, plotH);
                    }
                    p.Fill(FillRule.OddEven);

                    // Rim: a single stroke around the entire selected pixel span would
                    // require tracking contiguous runs — a simple column fill is enough
                    // at LOD density; skip per-column stroke to keep call count O(1).
                }
                else
                {
                    // ── Normal mode ─────────────────────────────────────────
                    float selY = plotY2 - plotH;

                    // Fill pass — per-bar rects so highlight only covers bars,
                    // not the gaps between them.  Single path + OddEven is
                    // already correct (no alpha accumulation).
                    p.fillColor = _vis.SelectionFillColor;
                    p.BeginPath();
                    for (int dispIdx = startDisp; dispIdx < endDisp; dispIdx++)
                    {
                        int dataIdx = dispIdx < _viewState.DisplayToData.Length
                            ? _viewState.DisplayToData[dispIdx] : dispIdx;
                        if (!_viewState.SelectedBars.Contains(dataIdx)) continue;
                        SnapBarX(dispIdx, plotX, stride, barW, out float sx, out float sw);
                        PathRect(p, sx, selY, sw, plotH);
                    }
                    p.Fill(FillRule.OddEven);

                    // Rim stroke pass — merge contiguous selected bars into
                    // runs to avoid per-bar stroke alpha accumulation that
                    // makes selection appear more opaque at higher zoom.
                    _selectionRuns.Clear();
                    int runStart = -1;
                    for (int dispIdx = startDisp; dispIdx < endDisp; dispIdx++)
                    {
                        int dataIdx = dispIdx < _viewState.DisplayToData.Length
                            ? _viewState.DisplayToData[dispIdx] : dispIdx;
                        bool selected = _viewState.SelectedBars.Contains(dataIdx);

                        if (selected && runStart < 0)
                            runStart = dispIdx;
                        else if (!selected && runStart >= 0)
                        {
                            _selectionRuns.Add(runStart);
                            _selectionRuns.Add(dispIdx);
                            runStart = -1;
                        }
                    }
                    if (runStart >= 0)
                    {
                        _selectionRuns.Add(runStart);
                        _selectionRuns.Add(endDisp);
                    }

                    p.strokeColor = _vis.SelectionRimColor;
                    p.lineWidth   = _vis.SelectionRimWidth;
                    float selInset = p.lineWidth * 0.5f + 0.5f;
                    for (int i = 0; i < _selectionRuns.Count; i += 2)
                    {
                        int rs = _selectionRuns[i];
                        int re = _selectionRuns[i + 1];
                        SnapBarX(rs,     plotX, stride, barW, out float rx, out _);
                        SnapBarX(re - 1, plotX, stride, barW, out float ex, out float ew);
                        float w = ex + ew - rx;
                        p.BeginPath();
                        PathRect(p, rx, selY + selInset, w, plotH - selInset * 2f);
                        p.Stroke();
                    }
                }
            }

            // ── Focused bar (keyboard focus ring) — always single-bar, always fast ──
            int focIdx = _viewState.FocusedBarIndex;
            if (!lodMode && focIdx >= 0 && focIdx < _model.BarCount)
            {
                int displayIdx = focIdx < _viewState.DataToDisplay.Length
                    ? _viewState.DataToDisplay[focIdx] : focIdx;
                SnapBarX(displayIdx, plotX, stride, barW, out float fx, out float fw);

                p.strokeColor = _vis.FocusRimColor;
                p.lineWidth   = 2f;
                float focInset = p.lineWidth * 0.5f + 0.5f;
                p.BeginPath();
                PathRect(p, fx - 1f, plotY2 - plotH + focInset, fw + 2f, plotH - focInset * 2f);
                p.Stroke();
            }

            // ── Hovered bar tint — always single-bar, always fast ──────────────
            // Skip when a segment is hovered — the segment highlight provides
            // feedback, and a full-bar tint would paint over segment selection.
            int hovIdx = _viewState.HoveredBarIndex;
            if (hovIdx >= 0 && hovIdx < _model.BarCount
                && (_viewState.HoveredSegmentIndex < 0 || GetSegmentCount(hovIdx) <= 1))
            {
                int displayIdx = hovIdx < _viewState.DataToDisplay.Length
                    ? _viewState.DataToDisplay[hovIdx] : hovIdx;
                SnapBarX(displayIdx, plotX, stride, barW, out float hx, out float hw);

                // In LOD mode use a 1 px wide tint; in normal mode use the full barW.
                if (lodMode) hw = 1f;

                p.fillColor = _vis.HoverTintColor;
                p.BeginPath();
                PathRect(p, hx, plotY2 - plotH, hw, plotH);
                p.Fill();
            }

            // ── Selected segment highlight (persistent until next click) ────────
            int selSegBar = _viewState.SelectedSegmentBar;
            int selSegIdx = _viewState.SelectedSegmentIndex;

            // In LOD mode, draw a marker column so the user can still see which
            // bar has the selected segment even when zoomed out.
            if (lodMode && _settings.ShowSegmentHighlightInLod
                && selSegBar >= 0 && selSegBar < _model.BarCount)
            {
                int displayIdx = selSegBar < _viewState.DataToDisplay.Length
                    ? _viewState.DataToDisplay[selSegBar] : selSegBar;
                if (displayIdx >= startDisp && displayIdx <= endDisp)
                {
                    int viewCount = endDisp - startDisp;
                    int pixelCount = Mathf.Max(1, Mathf.FloorToInt(plotW));
                    float px = plotX + (float)(displayIdx - startDisp) * pixelCount / viewCount;

                    p.fillColor = _vis.SelectionFillColor;
                    p.BeginPath();
                    PathRect(p, px, plotY2 - plotH, 1f, plotH);
                    p.Fill();

                    p.strokeColor = _vis.SegSelectionColor;
                    p.lineWidth   = _vis.SegSelectionWidth;
                    p.BeginPath();
                    PathRect(p, px, plotY2 - plotH, 1f, plotH);
                    p.Stroke();
                }
            }

            if (!lodMode && selSegBar >= 0 && selSegBar < _model.BarCount && selSegIdx >= 0)
            {
                ref readonly BarEntry selBar = ref _model.Bars[selSegBar];
                if (selSegIdx < selBar.SegmentCount)
                {
                    int displayIdx = selSegBar < _viewState.DataToDisplay.Length
                        ? _viewState.DataToDisplay[selSegBar] : selSegBar;
                    SnapBarX(displayIdx, plotX, stride, barW, out float sx, out float sw);

                    float yScale  = plotH / _effectiveMaxY * _viewState.ZoomY;
                    float yOffset = _viewState.PanY * plotH;
                    float yBottom = plotY2 + yOffset;

                    for (int s = 0; s <= selSegIdx; s++)
                    {
                        ref readonly BarSegment seg = ref _model.Segments[selBar.SegmentStart + s];
                        float segH = seg.Value * yScale;
                        if (s == selSegIdx)
                        {
                            float yTop    = yBottom - segH;
                            float drawTop = Mathf.Max(yTop, plotY2 - plotH);
                            float drawBot = Mathf.Min(yBottom, plotY2);
                            float drawH   = drawBot - drawTop;
                            if (drawH >= 0.5f)
                            {
                                // Outline only — visually distinct from hover's fill tint.
                                // Color and width controlled via USS:
                                //   --bar-graph-segment-selection-color
                                //   --bar-graph-segment-selection-width
                                p.strokeColor = _vis.SegSelectionColor;
                                p.lineWidth   = _vis.SegSelectionWidth;
                                p.BeginPath();
                                PathRect(p, sx, drawTop, sw, drawH);
                                p.Stroke();
                            }
                            break;
                        }
                        yBottom -= segH;
                    }
                }
            }

            // ── Tag highlight outline — all matching segments across visible bars ──
            // Disabled by default (outline width 0, transparent color). Only runs
            // when explicitly opted in via USS or C#.
            int tagHL = _viewState.HighlightedTag;
            float tagOutW = _vis.TagHighlightOutlineWidth;
            if (!lodMode && tagHL >= 0 && tagOutW > 0f && _vis.TagHighlightOutline.a > 0f)
            {
                p.strokeColor = _vis.TagHighlightOutline;
                p.lineWidth   = tagOutW;
                p.BeginPath();

                float yScale  = plotH / _effectiveMaxY * _viewState.ZoomY;
                float yOffset = _viewState.PanY * plotH;
                var tagFilter = _tagHighlightFilter;

                for (int dispIdx = startDisp; dispIdx < endDisp; dispIdx++)
                {
                    int dataIdx = _viewState.DisplayToData[dispIdx];
                    if (dataIdx >= _model.BarCount) continue;
                    if (tagFilter != null && !tagFilter.Invoke(dataIdx)) continue;

                    ref readonly BarEntry bar = ref _model.Bars[dataIdx];
                    SnapBarX(dispIdx, plotX, stride, barW, out float bx, out float bWidth);
                    float yBottom = plotY2 + yOffset;

                    for (int s = 0; s < bar.SegmentCount; s++)
                    {
                        ref readonly BarSegment seg = ref _model.Segments[bar.SegmentStart + s];
                        float segH = seg.Value * yScale;
                        if (seg.Tag == tagHL)
                        {
                            float yTop    = yBottom - segH;
                            float drawTop = Mathf.Max(yTop, plotY2 - plotH);
                            float drawBot = Mathf.Min(yBottom, plotY2);
                            float drawH   = drawBot - drawTop;
                            if (drawH >= 0.5f)
                                PathRect(p, bx, drawTop, bWidth, drawH);
                        }
                        yBottom -= segH;
                        if (yBottom <= plotY2 - plotH) break;
                    }
                }

                p.Stroke();
            }

            // ── Hovered segment highlight — single segment rect + outline ────────
            // Skip when hovering the already-selected segment so the selection
            // outline remains visible without hover tint painting over it.
            int segBar = _viewState.HoveredSegmentBar;
            int segIdx = _viewState.HoveredSegmentIndex;
            if (!lodMode && segBar >= 0 && segBar < _model.BarCount && segIdx >= 0
                && !(segBar == selSegBar && segIdx == selSegIdx))
            {
                ref readonly BarEntry bar = ref _model.Bars[segBar];
                if (segIdx < bar.SegmentCount)
                {
                    int displayIdx = segBar < _viewState.DataToDisplay.Length
                        ? _viewState.DataToDisplay[segBar] : segBar;
                    SnapBarX(displayIdx, plotX, stride, barW, out float sx, out float sw);

                    // Walk segments to find this segment's Y position
                    // (same math as DrawDirectBars / HitTestSegment)
                    float yScale  = plotH / _effectiveMaxY * _viewState.ZoomY;
                    float yOffset = _viewState.PanY * plotH;
                    float yBottom = plotY2 + yOffset;

                    for (int s = 0; s <= segIdx; s++)
                    {
                        ref readonly BarSegment seg = ref _model.Segments[bar.SegmentStart + s];
                        float segH = seg.Value * yScale;
                        if (s == segIdx)
                        {
                            float yTop    = yBottom - segH;
                            float drawTop = Mathf.Max(yTop, plotY2 - plotH);
                            float drawBot = Mathf.Min(yBottom, plotY2);
                            float drawH   = drawBot - drawTop;
                            if (drawH >= 0.5f)
                            {
                                // Tint fill
                                p.fillColor = _vis.HoverTintColor;
                                p.BeginPath();
                                PathRect(p, sx, drawTop, sw, drawH);
                                p.Fill();

                                // Outline
                                p.strokeColor = _vis.SelectionRimColor;
                                p.lineWidth   = _vis.SelectionRimWidth;
                                p.BeginPath();
                                PathRect(p, sx, drawTop, sw, drawH);
                                p.Stroke();
                            }
                            break;
                        }
                        yBottom -= segH;
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Drag-select rectangle
        // ─────────────────────────────────────────────────────────────────────

        private void DrawDragRect(Painter2D p, Rect r)
        {
            p.fillColor = _vis.DragRectFillColor;
            p.BeginPath();
            PathRect(p, r.x, r.y, r.width, r.height);
            p.Fill();

            p.strokeColor = _vis.DragRectBorderColor;
            p.lineWidth   = 1f;
            p.BeginPath();
            PathRect(p, r.x, r.y, r.width, r.height);
            p.Stroke();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Axes
        // ─────────────────────────────────────────────────────────────────────

        private void DrawAxes(Painter2D p,
            float plotX, float plotX2, float plotY, float plotY2)
        {
            p.strokeColor = _vis.AxisColor;
            p.lineWidth   = _vis.AxisLineWidth;
            p.BeginPath();
            p.MoveTo(new Vector2(plotX, plotY));
            p.LineTo(new Vector2(plotX, plotY2));
            p.Stroke();

            p.BeginPath();
            p.MoveTo(new Vector2(plotX,  plotY2));
            p.LineTo(new Vector2(plotX2, plotY2));
            p.Stroke();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Label overlay
        // ─────────────────────────────────────────────────────────────────────

        private void EnsureLabelPool()
        {
            int yCount = Mathf.Min(_settings.MaxYLabels + 1, _settings.GridLineCount + 1);
            if (_yLabels.Count == yCount && _xLabels.Count == _settings.MaxXLabels)
                return;
            RebuildLabelPool();
        }

        private void RebuildLabelPool()
        {
            _labelRoot.Clear();
            _yLabels.Clear();
            _xLabels.Clear();

            int yCount = Mathf.Min(_settings.MaxYLabels + 1, _settings.GridLineCount + 1);
            for (int i = 0; i < yCount; i++) { var l = MakeLabel("bar-graph__label--y"); _yLabels.Add(l); _labelRoot.Add(l); }
            for (int i = 0; i < _settings.MaxXLabels; i++) { var l = MakeLabel("bar-graph__label--x"); _xLabels.Add(l); _labelRoot.Add(l); }
        }

        /// <summary>
        /// Called by the per-frame scheduler.  Applies cached label positions
        /// computed during the last repaint — outside <c>generateVisualContent</c>
        /// so that VisualElement style mutations don't invalidate the layout pass.
        /// </summary>
        private void FlushLabelPositions()
        {
            if (!_labelsDirty) return;
            _labelsDirty = false;
            PositionLabels(_lblPlotX, _lblPlotW, _lblPlotH, _lblPlotY2);
        }

        private void PositionLabels(float plotX, float plotW, float plotH, float plotY2)
        {
            // Y-axis value labels.
            // The visible Y range is determined by ZoomY and PanY — not just
            // [MinValue, _effectiveMaxY].  From DrawDirectBars:
            //   yScale  = plotH / _effectiveMaxY * ZoomY
            //   yBottom = plotY2 + PanY * plotH
            // → value at screen-y = (yBottom - y) / yScale
            // → bottom edge (y=plotY2) : PanY * _effectiveMaxY / ZoomY
            // → top    edge (y=plotY2-plotH) : (1+PanY) * _effectiveMaxY / ZoomY
            float zoomY      = Mathf.Max(0.001f, _viewState.ZoomY);
            float visibleMin = _viewState.PanY         * _effectiveMaxY / zoomY;
            float visibleMax = (1f + _viewState.PanY)  * _effectiveMaxY / zoomY;

            for (int i = 0; i < _yLabels.Count; i++)
            {
                Label lbl  = _yLabels[i];
                int   cnt  = _yLabels.Count;
                float t    = cnt > 1 ? (float)i / (cnt - 1) : 0f;
                float val  = Mathf.Lerp(visibleMin, visibleMax, t);
                float y    = plotY2 - t * plotH;
                lbl.text   = _yFormatter(val);
                lbl.style.left   = 0f;
                lbl.style.top    = y - _vis.LabelHeight * 0.5f;
                lbl.style.width  = plotX - _vis.YLabelGap;
                lbl.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleRight);
                lbl.visible = true;
            }

            // X-axis bar labels — show as many as fit without overlapping
            CalcViewSlice(plotW, out float stride, out float barW, out int startDisp, out int endDisp);
            int viewCnt    = endDisp - startDisp;
            int showCount  = _vis.XLabelWidth > 0
                ? Mathf.Min(_xLabels.Count, Mathf.FloorToInt(plotW / _vis.XLabelWidth))
                : 0;
            bool show = showCount > 0 && viewCnt > 0;

            for (int j = 0; j < _xLabels.Count; j++)
            {
                Label lbl = _xLabels[j];
                if (!show || j >= showCount) { lbl.visible = false; continue; }

                float step = showCount > 1 ? (float)(viewCnt - 1) / (showCount - 1) : 0f;
                int   dispIdx = Mathf.Clamp(startDisp + Mathf.RoundToInt(j * step), startDisp, endDisp - 1);
                int   dataIdx = dispIdx < _viewState.DisplayToData.Length
                    ? _viewState.DisplayToData[dispIdx] : dispIdx;

                string text = null;
                if (dataIdx < _model.BarCount)
                {
                    text = _model.Bars[dataIdx].Label;
                    if (string.IsNullOrEmpty(text))
                        text = _xFormatter(dataIdx);
                }
                text ??= dispIdx.ToString();

                SnapBarX(dispIdx, plotX, stride, barW, out float sx, out float sw);
                float x = sx + sw * 0.5f;
                lbl.text = text;
                lbl.style.left   = x - _vis.XLabelWidth * 0.5f;
                lbl.style.top    = plotY2 + _vis.XLabelOffsetY;
                lbl.style.width  = _vis.XLabelWidth;
                lbl.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.UpperCenter);
                lbl.visible = (x >= plotX && x <= plotX + plotW);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Sort index (lazy rebuild, O(N log N) only when dirty)
        // ─────────────────────────────────────────────────────────────────────

        internal void EnsureSortMap()
        {
            if (!_viewState.SortDirty) return;
            int n = _model.BarCount;
            _viewState.EnsureSortCapacity(n);

            for (int i = 0; i < n; i++) _viewState.DisplayToData[i] = i;

            if (_viewState.SortMode == SortMode.ByValue)
            {
                bool desc = _viewState.SortDescending;
                Array.Sort(_viewState.DisplayToData, 0, n,
                    Comparer<int>.Create((a, b) =>
                    {
                        float va = _model.Bars[a].TotalValue;
                        float vb = _model.Bars[b].TotalValue;
                        return desc ? vb.CompareTo(va) : va.CompareTo(vb);
                    }));
            }

            for (int i = 0; i < n; i++)
                _viewState.DataToDisplay[_viewState.DisplayToData[i]] = i;

            _viewState.SortDirty = false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  View-slice calculation
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes which display indices [startDisp, endDisp) are visible given
        /// current zoom and pan.  <paramref name="stride"/> is the pixel width
        /// per bar slot (includes spacing); <paramref name="barW"/> is the filled
        /// portion.  Fractional PanX is preserved for sub-pixel accuracy.
        /// </summary>
        private void CalcViewSlice(float plotW,
            out float stride, out float barW,
            out int startDisp, out int endDisp)
        {
            int total = _model.BarCount;
            if (total == 0)
            {
                stride = plotW; barW = plotW; startDisp = 0; endDisp = 0; return;
            }

            // stride = plotW / total at zoom=1, scaled by ZoomX
            float baseStride = plotW / total;
            stride = baseStride * _viewState.ZoomX;
            float gap = stride * _vis.BarSpacingRatio;
            barW = Mathf.Max(_vis.MinBarWidthPx, stride - gap);

            // PanX is in data-space (fractional bar units)
            // How many bars fit in the viewport?
            float visibleBars = plotW / stride;

            startDisp = Mathf.FloorToInt(_viewState.PanX);
            startDisp = Mathf.Clamp(startDisp, 0, total - 1);

            // Add one extra on each side to avoid visible pop-in at edges
            int visCount = Mathf.CeilToInt(visibleBars) + 2;
            endDisp = Mathf.Clamp(startDisp + visCount, 0, total);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Pixel-snap helper (shared by draw, hit-test, highlights, labels)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes the pixel-perfect X position and width for bar at
        /// <paramref name="dispIdx"/> using a single Bresenham distribution
        /// over slot positions: <c>slotEdge(i) = floor(i * totalPx / N)</c>.
        ///
        /// Each slot is either <c>floor(totalPx/N)</c> or that +1 pixel wide,
        /// with the extra pixels maximally spread.  The gap within each slot
        /// is a fixed integer, so all gaps are identical.  Bar width absorbs
        /// the ±1 px slot variation, which is visually masked by the color fill.
        ///
        /// Guarantees:
        ///  • <c>slotRight[i] == slotLeft[i+1]</c> — slots tile perfectly.
        ///  • All gaps are exactly the same pixel width.
        ///  • Bar widths differ by at most 1 px, variation maximally spread.
        ///  • Total coverage == virtual canvas width (no remainder).
        /// </summary>
        private void SnapBarX(int dispIdx, float plotX, float stride, float barW,
                              out float snapX, out float snapW)
        {
            int N = _model.BarCount;
            if (N <= 0) { snapX = plotX; snapW = 1f; return; }

            // Total virtual pixel width for all slots at current zoom.
            int totalPx = Mathf.RoundToInt(N * stride);

            // Fixed integer gap — same for every slot.
            int gapPx = Mathf.Max(0, Mathf.RoundToInt(stride - barW));

            // Bresenham: left edge of this slot and next slot.
            int slotLeft  = (int)((long)dispIdx       * totalPx / N);
            int slotRight = (int)((long)(dispIdx + 1) * totalPx / N);
            int slotW     = slotRight - slotLeft;

            // Bar fills the slot minus the fixed gap.
            int bw = Mathf.Max(1, slotW - gapPx);

            snapX = plotX + slotLeft - _viewState.PanX * stride;
            snapW = bw;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Drag-select hit collection
        // ─────────────────────────────────────────────────────────────────────

        private void CollectBarsInRect(Rect rect, HashSet<int> result)
        {
            if (_model.BarCount == 0) return;

            float plotX  = _vis.PaddingLeft;
            float plotY2 = contentRect.height - _vis.PaddingBottom;
            float plotH  = GetPlotHeight();
            float plotW  = GetPlotWidth();

            CalcViewSlice(plotW, out float stride, out float barW,
                          out int startDisp, out int endDisp);

            EnsureSortMap();

            int viewCount = endDisp - startDisp;
            if (viewCount <= 0) return;

            if (stride < 1f)
            {
                // LOD mode: bars are denser than 1 px.
                // DrawLodBars maps display-index offset i → pixel column:
                //   px = Floor(i / viewCount * pixelCount)
                // Inverse: pixel column px → display-index offset start:
                //   i = Floor(px * viewCount / pixelCount)
                //
                // Convert the rect's x-range to a display-index range using the
                // same formula so only bars whose pixel columns fall inside the
                // drag rect get selected — not the entire visible set.
                int pixelCount = Mathf.Max(1, Mathf.FloorToInt(plotW));

                // Clamp rect to plot area, convert to pixel columns.
                float relXMin = Mathf.Clamp(rect.xMin - plotX, 0f, plotW);
                float relXMax = Mathf.Clamp(rect.xMax - plotX, 0f, plotW);

                // Pixel columns that the rect covers (inclusive).
                int pxMin = Mathf.FloorToInt(relXMin);
                int pxMax = Mathf.Min(Mathf.CeilToInt(relXMax), pixelCount - 1);

                if (pxMin > pxMax) return;

                // Map pixel column range → display-index range using inverse formula.
                int dispStart = startDisp + Mathf.FloorToInt((float)pxMin * viewCount / pixelCount);
                int dispEnd   = startDisp + Mathf.Min(
                    Mathf.CeilToInt((float)(pxMax + 1) * viewCount / pixelCount), viewCount);

                dispStart = Mathf.Clamp(dispStart, startDisp, endDisp);
                dispEnd   = Mathf.Clamp(dispEnd,   startDisp, endDisp);

                for (int dispIdx = dispStart; dispIdx < dispEnd; dispIdx++)
                {
                    int dataIdx = dispIdx < _viewState.DisplayToData.Length
                        ? _viewState.DisplayToData[dispIdx] : dispIdx;
                    if (dataIdx < _model.BarCount)
                        result.Add(dataIdx);
                }
                return;
            }

            // Normal mode: walk visible display indices, early-exit on x > rect.xMax.
            for (int dispIdx = startDisp; dispIdx < endDisp; dispIdx++)
            {
                SnapBarX(dispIdx, plotX, stride, barW, out float x, out float bw);
                if (x + bw < rect.xMin) continue;
                if (x > rect.xMax)      break;

                int dataIdx = dispIdx < _viewState.DisplayToData.Length
                    ? _viewState.DisplayToData[dispIdx] : dispIdx;
                if (dataIdx >= _model.BarCount) continue;

                float barH    = Mathf.Min(_model.Bars[dataIdx].TotalValue /
                                          _effectiveMaxY * plotH * _viewState.ZoomY, plotH);
                float barTopY = plotY2 - barH;
                var   barRect = new Rect(x, barTopY, bw, barH);

                if (rect.Overlaps(barRect))
                    result.Add(dataIdx);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Internal state helpers
        // ─────────────────────────────────────────────────────────────────────

        private void OnDataChanged()
        {
            _viewState.SelectedBars.Clear();
            _viewState.SelectedSegmentBar   = -1;
            _viewState.SelectedSegmentIndex = -1;
            _viewState.HoveredBarIndex      = -1;
            _viewState.HoveredSegmentBar    = -1;
            _viewState.HoveredSegmentIndex  = -1;
            _viewState.FocusedBarIndex      = -1;

            RecalcBounds();
            _viewState.SortDirty = true;
            EnsureLabelPool();
            MarkDirtyRepaint();
        }

        private void OnOverlayChanged()
        {
            MarkDirtyRepaint();
        }

        private void RecalcBounds()
        {
            _effectiveMaxY = _settings.MaxValue > 0f
                ? _settings.MaxValue
                : _model.MaxPrimaryY;
            if (_effectiveMaxY <= _settings.MinValue) _effectiveMaxY = _settings.MinValue + 1f;
        }

        private void ClampViewState()
        {
            float plotW       = GetPlotWidth();
            float stride      = GetBarStrideBase();
            float visibleBars = stride > 0f
                ? plotW / (stride * _viewState.ZoomX) : (float)_model.BarCount;

            _viewState.ClampPan(visibleBars, _model.BarCount, _viewState.ZoomY);
        }

        private void NotifyViewChanged()
        {
            ViewChanged?.Invoke(new ViewChangedEventArgs(
                _viewState.ZoomX, _viewState.ZoomY,
                _viewState.PanX,  _viewState.PanY,
                _viewState.SortMode, _viewState.SortDescending));
        }

        private void FireSelectionChanged()
        {
            SelectionChanged?.Invoke(new SelectionChangedEventArgs(_viewState.SelectedBars));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Direct mesh quad buffer (zero GC, bypasses Painter2D tessellator)
        // ─────────────────────────────────────────────────────────────────────

        private void AddQuad(Color32 color, float x, float y, float w, float h)
        {
            if (_quadCount == _quadBuf.Length)
                Array.Resize(ref _quadBuf, _quadBuf.Length * 2);

            _quadBuf[_quadCount++] = new QuadData { X = x, Y = y, W = w, H = h, Color = color };
        }

        /// <summary>
        /// Writes all buffered quads to the GPU via chunked
        /// <see cref="MeshGenerationContext.Allocate"/> calls.
        /// Each quad = 4 vertices + 6 indices.  Max 16 383 quads per chunk
        /// (65 532 vertices, under the 65 535 UInt16 index limit).
        /// Multiple chunks per element are supported — no ceiling on total quads.
        /// </summary>
        private void FlushQuads(MeshGenerationContext mgc)
        {
            const int MAX_QUADS_PER_CHUNK = 16383;   // 65 532 / 4
            const int VERTS_PER_QUAD  = 4;
            const int INDICES_PER_QUAD = 6;

            int offset = 0;
            while (offset < _quadCount)
            {
                int chunkQuads = Math.Min(MAX_QUADS_PER_CHUNK, _quadCount - offset);
                int vertCount  = chunkQuads * VERTS_PER_QUAD;
                int idxCount   = chunkQuads * INDICES_PER_QUAD;

                // Pass Texture2D.whiteTexture so that texture × tint = tint.
                MeshWriteData mwd = mgc.Allocate(vertCount, idxCount, Texture2D.whiteTexture);
                Vector2 uv = new Vector2(0.5f, 0.5f);

                for (int i = 0; i < chunkQuads; i++)
                {
                    ref QuadData q = ref _quadBuf[offset + i];
                    ushort vi = (ushort)(i * 4);

                    // Four corners: TL, TR, BR, BL
                    mwd.SetNextVertex(new Vertex
                    {
                        position = new Vector3(q.X,       q.Y,       Vertex.nearZ),
                        tint     = q.Color,
                        uv       = uv
                    });
                    mwd.SetNextVertex(new Vertex
                    {
                        position = new Vector3(q.X + q.W, q.Y,       Vertex.nearZ),
                        tint     = q.Color,
                        uv       = uv
                    });
                    mwd.SetNextVertex(new Vertex
                    {
                        position = new Vector3(q.X + q.W, q.Y + q.H, Vertex.nearZ),
                        tint     = q.Color,
                        uv       = uv
                    });
                    mwd.SetNextVertex(new Vertex
                    {
                        position = new Vector3(q.X,       q.Y + q.H, Vertex.nearZ),
                        tint     = q.Color,
                        uv       = uv
                    });

                    // Two triangles: TL-TR-BR, TL-BR-BL
                    mwd.SetNextIndex(vi);
                    mwd.SetNextIndex((ushort)(vi + 1));
                    mwd.SetNextIndex((ushort)(vi + 2));
                    mwd.SetNextIndex(vi);
                    mwd.SetNextIndex((ushort)(vi + 2));
                    mwd.SetNextIndex((ushort)(vi + 3));
                }
                offset += chunkQuads;
            }
            _quadCount = 0;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Painter2D utilities
        // ─────────────────────────────────────────────────────────────────────

        private static void FillRect(Painter2D p, Color c, float x, float y, float w, float h)
        {
            p.fillColor = c;
            p.BeginPath();
            p.MoveTo(new Vector2(x,     y));
            p.LineTo(new Vector2(x + w, y));
            p.LineTo(new Vector2(x + w, y + h));
            p.LineTo(new Vector2(x,     y + h));
            p.ClosePath();
            p.Fill();
        }

        private static void PathRect(Painter2D p, float x, float y, float w, float h)
        {
            p.MoveTo(new Vector2(x,     y));
            p.LineTo(new Vector2(x + w, y));
            p.LineTo(new Vector2(x + w, y + h));
            p.LineTo(new Vector2(x,     y + h));
            p.ClosePath();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Label helpers
        // ─────────────────────────────────────────────────────────────────────

        private Label MakeLabel(string ussClass)
        {
            var l = new Label
            {
                pickingMode = PickingMode.Ignore,
                style =
                {
                    position     = Position.Absolute,
                    overflow     = Overflow.Hidden,
                    paddingLeft  = 0, paddingRight  = 0,
                    marginLeft   = 0, marginRight   = 0,
                }
            };
            l.AddToClassList(ussClass);
            return l;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Color resolve
        // ─────────────────────────────────────────────────────────────────────

        private Color32 ResolveSegmentColor(Color32 c, float alpha)
        {
            // default(Color32) == (0,0,0,0) → use default bar colour
            bool isDefault = c.r == 0 && c.g == 0 && c.b == 0 && c.a == 0;
            Color32 resolved = isDefault ? (Color32)_vis.DefaultBarColor : c;

            if (alpha < 0.999f)
            {
                resolved.a = (byte)Mathf.RoundToInt(resolved.a * alpha);
            }
            return resolved;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Private structs
        // ─────────────────────────────────────────────────────────────────────

        private struct LodPixel
        {
            public float   MaxValue;
            public Color32 Color;
            public int     DataIndex;
        }

        /// <summary>
        /// Lightweight quad descriptor: position, size, and colour.
        /// Buffered by <see cref="AddQuad"/>, flushed to GPU by <see cref="FlushQuads"/>.
        /// </summary>
        private struct QuadData
        {
            public float   X, Y, W, H;
            public Color32 Color;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Segment interaction types
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Result of a segment hit-test.  Returned by
    /// <see cref="BarGraphElement.HitTestSegment"/>.
    /// </summary>
    public struct SegmentHitResult
    {
        /// <summary>Data index of the bar containing the hit segment, or -1.</summary>
        public int     BarDataIndex;

        /// <summary>Index within the bar's segment array, or -1.</summary>
        public int     SegmentIndex;

        /// <summary>User-defined tag from <see cref="BarSegment.Tag"/>, or -1.</summary>
        public int     Tag;

        /// <summary>Segment value (magnitude).</summary>
        public float   Value;

        /// <summary>Segment fill colour.</summary>
        public Color32 Color;

        /// <summary>True when the hit-test resolved to a valid segment.</summary>
        public bool IsHit => BarDataIndex >= 0 && SegmentIndex >= 0;

        /// <summary>Sentinel for no-hit / miss results.</summary>
        public static readonly SegmentHitResult Miss = new SegmentHitResult
        {
            BarDataIndex = -1, SegmentIndex = -1, Tag = -1
        };
    }

    /// <summary>
    /// Event args for segment interaction and
    /// <see cref="BarGraphElement.SegmentHoverChanged"/> events.
    /// </summary>
    public struct SegmentEventArgs
    {
        /// <summary>Data index of the bar containing the segment.</summary>
        public int     BarDataIndex;

        /// <summary>Display index of the bar (after sort mapping).</summary>
        public int     BarDisplayIndex;

        /// <summary>Index within the bar's segment array.</summary>
        public int     SegmentIndex;

        /// <summary>User-defined tag from <see cref="BarSegment.Tag"/>. -1 if anonymous.</summary>
        public int     Tag;

        /// <summary>Segment value (magnitude).</summary>
        public float   Value;

        /// <summary>Segment fill colour.</summary>
        public Color32 Color;

        /// <summary>Cursor position in element-local coordinates at time of event.</summary>
        public Vector2 LocalPosition;
    }
}
