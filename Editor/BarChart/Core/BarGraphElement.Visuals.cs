using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace GCAllocBreakdown.BarChart.Core
{
    /// <summary>
    /// Identifies a visual property in <see cref="BarGraphElement"/> for
    /// programmatic override tracking.  Used with <see cref="BarGraphElement.HasVisualOverride"/>
    /// and <see cref="BarGraphElement.ResetToUSS(VisualProperty)"/>.
    /// </summary>
    [Flags]
    public enum VisualProperty : uint
    {
        None                = 0,

        // ── Colors (bits 0–11) ──────────────────────────────────────────────
        BackgroundColor     = 1u << 0,
        DefaultBarColor     = 1u << 1,
        AxisColor           = 1u << 2,
        GridLineColor       = 1u << 3,
        HoverTintColor      = 1u << 4,
        SelectionFillColor  = 1u << 5,
        SelectionRimColor   = 1u << 6,
        SegSelectionColor   = 1u << 7,
        DragRectFillColor   = 1u << 8,
        DragRectBorderColor = 1u << 9,
        FocusRimColor       = 1u << 10,
        OverlayTint         = 1u << 11,

        // ── Floats (bits 12–27) ─────────────────────────────────────────────
        SelectionRimWidth   = 1u << 12,
        SegSelectionWidth   = 1u << 13,
        OverlayOpacity      = 1u << 14,
        BarSpacingRatio     = 1u << 15,
        MinBarWidthPx       = 1u << 16,
        GridLineWidth       = 1u << 17,
        AxisLineWidth       = 1u << 18,
        PaddingLeft         = 1u << 19,
        PaddingRight        = 1u << 20,
        PaddingTop          = 1u << 21,
        PaddingBottom       = 1u << 22,
        LabelHeight         = 1u << 23,
        XLabelWidth         = 1u << 24,
        XLabelOffsetY       = 1u << 25,
        YLabelGap           = 1u << 26,
        DimOpacity          = 1u << 27,
        TagHighlightTint         = 1u << 28,
        TagHighlightOutline      = 1u << 29,
        TagHighlightOutlineWidth = 1u << 30,

        // ── Convenience masks ───────────────────────────────────────────────
        AllColors           = 0x3000_0FFFu,  // bits 0–11, 28–29
        AllFloats           = 0x4FFF_F000u,  // bits 12–27, 30
        All                 = 0x7FFF_FFFFu,  // bits 0–30
    }

    public sealed partial class BarGraphElement
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Override tracking
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Bitfield tracking which <see cref="_vis"/> fields have C# overrides.</summary>
        private uint _visOverrides;

        /// <summary>Compile-time defaults — used as fallback when both USS and C# override are absent.</summary>
        private static readonly ResolvedVisuals DefaultVisuals = new ResolvedVisuals
        {
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

        // ─────────────────────────────────────────────────────────────────────
        //  Override helpers (shared by all property setters)
        // ─────────────────────────────────────────────────────────────────────

        private bool HasOverride(VisualProperty flag)
            => (_visOverrides & (uint)flag) != 0;

        private void SetColorOverride(VisualProperty flag, ref Color field, Color value)
        {
            field = value;
            _visOverrides |= (uint)flag;
            MarkDirtyRepaint();
        }

        private void SetFloatOverride(
            VisualProperty flag, ref float field, float value,
            float min = float.MinValue, float max = float.MaxValue)
        {
            field = Mathf.Clamp(value, min, max);
            _visOverrides |= (uint)flag;
            MarkDirtyRepaint();
        }

        private static Color GetDefaultColor(VisualProperty flag) => flag switch
        {
            VisualProperty.BackgroundColor     => DefaultVisuals.BackgroundColor,
            VisualProperty.DefaultBarColor     => DefaultVisuals.DefaultBarColor,
            VisualProperty.AxisColor           => DefaultVisuals.AxisColor,
            VisualProperty.GridLineColor       => DefaultVisuals.GridLineColor,
            VisualProperty.HoverTintColor      => DefaultVisuals.HoverTintColor,
            VisualProperty.SelectionFillColor  => DefaultVisuals.SelectionFillColor,
            VisualProperty.SelectionRimColor   => DefaultVisuals.SelectionRimColor,
            VisualProperty.SegSelectionColor   => DefaultVisuals.SegSelectionColor,
            VisualProperty.DragRectFillColor   => DefaultVisuals.DragRectFillColor,
            VisualProperty.DragRectBorderColor => DefaultVisuals.DragRectBorderColor,
            VisualProperty.FocusRimColor       => DefaultVisuals.FocusRimColor,
            VisualProperty.OverlayTint         => DefaultVisuals.OverlayTint,
            VisualProperty.TagHighlightTint    => DefaultVisuals.TagHighlightTint,
            VisualProperty.TagHighlightOutline => DefaultVisuals.TagHighlightOutline,
            _ => Color.magenta,
        };

        private static float GetDefaultFloat(VisualProperty flag) => flag switch
        {
            VisualProperty.SelectionRimWidth => DefaultVisuals.SelectionRimWidth,
            VisualProperty.SegSelectionWidth => DefaultVisuals.SegSelectionWidth,
            VisualProperty.OverlayOpacity    => DefaultVisuals.OverlayOpacity,
            VisualProperty.BarSpacingRatio   => DefaultVisuals.BarSpacingRatio,
            VisualProperty.MinBarWidthPx     => DefaultVisuals.MinBarWidthPx,
            VisualProperty.GridLineWidth     => DefaultVisuals.GridLineWidth,
            VisualProperty.AxisLineWidth     => DefaultVisuals.AxisLineWidth,
            VisualProperty.PaddingLeft       => DefaultVisuals.PaddingLeft,
            VisualProperty.PaddingRight      => DefaultVisuals.PaddingRight,
            VisualProperty.PaddingTop        => DefaultVisuals.PaddingTop,
            VisualProperty.PaddingBottom     => DefaultVisuals.PaddingBottom,
            VisualProperty.LabelHeight       => DefaultVisuals.LabelHeight,
            VisualProperty.XLabelWidth       => DefaultVisuals.XLabelWidth,
            VisualProperty.XLabelOffsetY     => DefaultVisuals.XLabelOffsetY,
            VisualProperty.YLabelGap         => DefaultVisuals.YLabelGap,
            VisualProperty.DimOpacity               => DefaultVisuals.DimOpacity,
            VisualProperty.TagHighlightOutlineWidth => DefaultVisuals.TagHighlightOutlineWidth,
            _ => 0f,
        };

        // ─────────────────────────────────────────────────────────────────────
        //  USS resolve helpers (called from ResolveCustomStyles)
        // ─────────────────────────────────────────────────────────────────────

        private void TryResolveColor(CustomStyleProperty<Color> prop, VisualProperty flag, ref Color field)
        {
            if ((_visOverrides & (uint)flag) != 0) return;
            if (customStyle.TryGetValue(prop, out var c)) field = c;
        }

        private void TryResolveFloat(CustomStyleProperty<float> prop, VisualProperty flag, ref float field)
        {
            if ((_visOverrides & (uint)flag) != 0) return;
            if (customStyle.TryGetValue(prop, out var f)) field = f;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public visual property API – Colors
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Background fill colour. Use <see cref="ResetToUSS"/> to revert.</summary>
        public Color VisBackgroundColor
        {
            get => _vis.BackgroundColor;
            set => SetColorOverride(VisualProperty.BackgroundColor, ref _vis.BackgroundColor, value);
        }

        /// <summary>
        /// Default bar fill colour (used when segment colour is <c>default</c>).
        /// Use <see cref="ResetToUSS"/> to revert.
        /// </summary>
        public Color VisDefaultBarColor
        {
            get => _vis.DefaultBarColor;
            set => SetColorOverride(VisualProperty.DefaultBarColor, ref _vis.DefaultBarColor, value);
        }

        /// <summary>Axis line colour. Use <see cref="ResetToUSS"/> to revert.</summary>
        public Color VisAxisColor
        {
            get => _vis.AxisColor;
            set => SetColorOverride(VisualProperty.AxisColor, ref _vis.AxisColor, value);
        }

        /// <summary>Grid line colour. Use <see cref="ResetToUSS"/> to revert.</summary>
        public Color VisGridLineColor
        {
            get => _vis.GridLineColor;
            set => SetColorOverride(VisualProperty.GridLineColor, ref _vis.GridLineColor, value);
        }

        /// <summary>Hover tint overlay. Use <see cref="ResetToUSS"/> to revert.</summary>
        public Color VisHoverTintColor
        {
            get => _vis.HoverTintColor;
            set => SetColorOverride(VisualProperty.HoverTintColor, ref _vis.HoverTintColor, value);
        }

        /// <summary>Selection fill colour. Use <see cref="ResetToUSS"/> to revert.</summary>
        public Color VisSelectionFillColor
        {
            get => _vis.SelectionFillColor;
            set => SetColorOverride(VisualProperty.SelectionFillColor, ref _vis.SelectionFillColor, value);
        }

        /// <summary>Selection rim colour. Use <see cref="ResetToUSS"/> to revert.</summary>
        public Color VisSelectionRimColor
        {
            get => _vis.SelectionRimColor;
            set => SetColorOverride(VisualProperty.SelectionRimColor, ref _vis.SelectionRimColor, value);
        }

        /// <summary>Segment selection outline colour. Use <see cref="ResetToUSS"/> to revert.</summary>
        public Color VisSegSelectionColor
        {
            get => _vis.SegSelectionColor;
            set => SetColorOverride(VisualProperty.SegSelectionColor, ref _vis.SegSelectionColor, value);
        }

        /// <summary>Drag-select rectangle fill. Use <see cref="ResetToUSS"/> to revert.</summary>
        public Color VisDragRectFillColor
        {
            get => _vis.DragRectFillColor;
            set => SetColorOverride(VisualProperty.DragRectFillColor, ref _vis.DragRectFillColor, value);
        }

        /// <summary>Drag-select rectangle border. Use <see cref="ResetToUSS"/> to revert.</summary>
        public Color VisDragRectBorderColor
        {
            get => _vis.DragRectBorderColor;
            set => SetColorOverride(VisualProperty.DragRectBorderColor, ref _vis.DragRectBorderColor, value);
        }

        /// <summary>Focused bar rim colour. Use <see cref="ResetToUSS"/> to revert.</summary>
        public Color VisFocusRimColor
        {
            get => _vis.FocusRimColor;
            set => SetColorOverride(VisualProperty.FocusRimColor, ref _vis.FocusRimColor, value);
        }

        /// <summary>Overlay series tint colour. Use <see cref="ResetToUSS"/> to revert.</summary>
        public Color VisOverlayTint
        {
            get => _vis.OverlayTint;
            set => SetColorOverride(VisualProperty.OverlayTint, ref _vis.OverlayTint, value);
        }

        /// <summary>Tint fill applied to segments matching <see cref="HighlightedTag"/>. Use <see cref="ResetToUSS"/> to revert.</summary>
        public Color VisTagHighlightTint
        {
            get => _vis.TagHighlightTint;
            set => SetColorOverride(VisualProperty.TagHighlightTint, ref _vis.TagHighlightTint, value);
        }

        /// <summary>Outline colour for segments matching <see cref="HighlightedTag"/>. Transparent = disabled. Use <see cref="ResetToUSS"/> to revert.</summary>
        public Color VisTagHighlightOutline
        {
            get => _vis.TagHighlightOutline;
            set => SetColorOverride(VisualProperty.TagHighlightOutline, ref _vis.TagHighlightOutline, value);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public visual property API – Floats
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Selection rim stroke width (px). Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisSelectionRimWidth
        {
            get => _vis.SelectionRimWidth;
            set => SetFloatOverride(VisualProperty.SelectionRimWidth, ref _vis.SelectionRimWidth, value, 0f);
        }

        /// <summary>Segment selection outline width (px). Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisSegSelectionWidth
        {
            get => _vis.SegSelectionWidth;
            set => SetFloatOverride(VisualProperty.SegSelectionWidth, ref _vis.SegSelectionWidth, value, 0f);
        }

        /// <summary>Overlay series opacity (0–1). Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisOverlayOpacity
        {
            get => _vis.OverlayOpacity;
            set => SetFloatOverride(VisualProperty.OverlayOpacity, ref _vis.OverlayOpacity, value, 0f, 1f);
        }

        /// <summary>
        /// Gap between bars as a fraction of slot width (0 = contiguous, 0.12 = 12% gap).
        /// Use <see cref="ResetToUSS"/> to revert.
        /// </summary>
        public float VisBarSpacingRatio
        {
            get => _vis.BarSpacingRatio;
            set => SetFloatOverride(VisualProperty.BarSpacingRatio, ref _vis.BarSpacingRatio, value, 0f, 1f);
        }

        /// <summary>Minimum bar width in pixels. Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisMinBarWidthPx
        {
            get => _vis.MinBarWidthPx;
            set => SetFloatOverride(VisualProperty.MinBarWidthPx, ref _vis.MinBarWidthPx, value, 0f);
        }

        /// <summary>Grid line width (px). Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisGridLineWidth
        {
            get => _vis.GridLineWidth;
            set => SetFloatOverride(VisualProperty.GridLineWidth, ref _vis.GridLineWidth, value, 0f);
        }

        /// <summary>Axis line width (px). Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisAxisLineWidth
        {
            get => _vis.AxisLineWidth;
            set => SetFloatOverride(VisualProperty.AxisLineWidth, ref _vis.AxisLineWidth, value, 0f);
        }

        /// <summary>Left padding (px). Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisPaddingLeft
        {
            get => _vis.PaddingLeft;
            set
            {
                SetFloatOverride(VisualProperty.PaddingLeft, ref _vis.PaddingLeft, value, 0f);
                RebuildLabelPool();
            }
        }

        /// <summary>Right padding (px). Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisPaddingRight
        {
            get => _vis.PaddingRight;
            set
            {
                SetFloatOverride(VisualProperty.PaddingRight, ref _vis.PaddingRight, value, 0f);
                RebuildLabelPool();
            }
        }

        /// <summary>Top padding (px). Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisPaddingTop
        {
            get => _vis.PaddingTop;
            set
            {
                SetFloatOverride(VisualProperty.PaddingTop, ref _vis.PaddingTop, value, 0f);
                RebuildLabelPool();
            }
        }

        /// <summary>Bottom padding (px). Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisPaddingBottom
        {
            get => _vis.PaddingBottom;
            set
            {
                SetFloatOverride(VisualProperty.PaddingBottom, ref _vis.PaddingBottom, value, 0f);
                RebuildLabelPool();
            }
        }

        /// <summary>Label height (px). Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisLabelHeight
        {
            get => _vis.LabelHeight;
            set
            {
                SetFloatOverride(VisualProperty.LabelHeight, ref _vis.LabelHeight, value, 0f);
                RebuildLabelPool();
            }
        }

        /// <summary>X-axis label width (px). Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisXLabelWidth
        {
            get => _vis.XLabelWidth;
            set => SetFloatOverride(VisualProperty.XLabelWidth, ref _vis.XLabelWidth, value, 0f);
        }

        /// <summary>X-axis label vertical offset (px). Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisXLabelOffsetY
        {
            get => _vis.XLabelOffsetY;
            set => SetFloatOverride(VisualProperty.XLabelOffsetY, ref _vis.XLabelOffsetY, value);
        }

        /// <summary>Gap between Y-axis labels and the plot area (px). Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisYLabelGap
        {
            get => _vis.YLabelGap;
            set => SetFloatOverride(VisualProperty.YLabelGap, ref _vis.YLabelGap, value, 0f);
        }

        /// <summary>
        /// Opacity multiplier applied to non-selected bars when a selection is active (0–1).
        /// Use <see cref="ResetToUSS"/> to revert.
        /// </summary>
        public float VisDimOpacity
        {
            get => _vis.DimOpacity;
            set => SetFloatOverride(VisualProperty.DimOpacity, ref _vis.DimOpacity, value, 0f, 1f);
        }

        /// <summary>Outline width (px) for segments matching <see cref="HighlightedTag"/>. 0 = disabled. Use <see cref="ResetToUSS"/> to revert.</summary>
        public float VisTagHighlightOutlineWidth
        {
            get => _vis.TagHighlightOutlineWidth;
            set => SetFloatOverride(VisualProperty.TagHighlightOutlineWidth, ref _vis.TagHighlightOutlineWidth, value, 0f);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Override management
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> if any C# override is active for the specified property
        /// (or any property in a flags combination).
        /// </summary>
        public bool HasVisualOverride(VisualProperty property)
            => (_visOverrides & (uint)property) != 0;

        /// <summary>
        /// Reverts all visual properties to their USS-resolved (or default) values.
        /// </summary>
        public void ResetToUSS()
        {
            _visOverrides = 0;
            ResolveCustomStyles();
        }

        /// <summary>
        /// Reverts the specified visual properties to their USS-resolved (or default) values.
        /// </summary>
        public void ResetToUSS(VisualProperty properties)
        {
            _visOverrides &= ~(uint)properties;
            ResolveCustomStyles();
        }
    }
}
