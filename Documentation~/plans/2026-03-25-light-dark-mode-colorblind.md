# Plan: Light Mode, Dark Mode, and Color Blind Accessibility

## Overview

Add themeable light/dark mode support and color blind accessibility to the bar graph. All visual changes are USS-only — no C# code changes to the graph element. Data colors (bar/segment fills) are the consumer's responsibility but the plan includes recommended palettes and a `BarVisualProvider` pattern for runtime remapping.

There are two distinct color concerns:

1. **Chrome colors** — grid, axes, selection, hover, focus, labels, background. Controlled by USS custom properties. The graph element owns these.
2. **Data colors** — bar and segment fills set via `BarSegment.Color`. The consumer owns these. The graph provides `BarVisualProvider` as a render-time override hook.

---

## Part 1: Light and Dark Mode

### Approach

Two USS class selectors: `.bar-graph--dark` (default) and `.bar-graph--light`. Swap the class to switch modes. All chrome properties get appropriate values for each mode.

### Dark Mode (default — current look)

Values match the current `.bar-graph` defaults exactly so the graph looks identical when `.bar-graph--dark` is applied.

```css
.bar-graph--dark {
    --bar-graph-bg-color:                rgb(18, 18, 26);
    --bar-graph-default-bar-color:       rgb(64, 153, 255);
    --bar-graph-axis-color:              rgb(140, 140, 153);
    --bar-graph-grid-line-color:         rgb(46, 46, 61);
    --bar-graph-hover-tint-color:        rgba(255, 255, 255, 0.18);
    --bar-graph-selection-fill-color:    rgba(255, 255, 255, 0.22);
    --bar-graph-selection-rim-color:     rgba(102, 179, 255, 0.9);
    --bar-graph-segment-selection-color: rgb(255, 204, 51);
    --bar-graph-drag-rect-fill-color:    rgba(89, 166, 255, 0.08);
    --bar-graph-drag-rect-border-color:  rgba(89, 166, 255, 0.6);
    --bar-graph-focus-rim-color:         rgb(255, 204, 51);
    --bar-graph-overlay-tint:            rgba(255, 255, 255, 0.38);
    --bar-graph-tag-highlight-tint:      rgba(255, 255, 255, 0.22);
    --bar-graph-tag-highlight-outline:   rgba(0, 0, 0, 0);
}

.bar-graph--dark .bar-graph__label--y,
.bar-graph--dark .bar-graph__label--x {
    color: rgb(191, 191, 199);
}
```

### Light Mode

Light mode inverts the contrast model: dark chrome on a light background. Key considerations:
- Background goes near-white, not pure white (reduces glare)
- Grid lines become darker but stay subtle
- Selection and hover use darker tints instead of lighter ones
- Axis and label colors need sufficient contrast against the light background (WCAG AA: 4.5:1 for text)
- Overlay and tag-highlight tints use dark overlays instead of light

```css
.bar-graph--light {
    --bar-graph-bg-color:                rgb(248, 248, 250);
    --bar-graph-default-bar-color:       rgb(50, 120, 220);
    --bar-graph-axis-color:              rgb(80, 80, 95);
    --bar-graph-grid-line-color:         rgba(0, 0, 0, 0.07);
    --bar-graph-hover-tint-color:        rgba(0, 0, 0, 0.12);
    --bar-graph-selection-fill-color:    rgba(0, 0, 0, 0.14);
    --bar-graph-selection-rim-color:     rgba(30, 100, 200, 0.9);
    --bar-graph-segment-selection-color: rgb(200, 140, 0);
    --bar-graph-drag-rect-fill-color:    rgba(30, 100, 200, 0.1);
    --bar-graph-drag-rect-border-color:  rgba(30, 100, 200, 0.63);
    --bar-graph-focus-rim-color:         rgb(200, 140, 0);
    --bar-graph-overlay-tint:            rgba(0, 0, 0, 0.25);
    --bar-graph-tag-highlight-tint:      rgba(0, 0, 0, 0.15);
    --bar-graph-tag-highlight-outline:   rgba(0, 0, 0, 0);
}

.bar-graph--light .bar-graph__label--y,
.bar-graph--light .bar-graph__label--x {
    color: rgb(50, 50, 60);
}
```

### Switching Modes

```csharp
// Toggle between modes
graph.RemoveFromClassList("bar-graph--dark");
graph.AddToClassList("bar-graph--light");
```

USS resolves automatically on class change → `CustomStyleResolvedEvent` fires → `_vis` updates → repaint.

### Unity Editor Integration

For EditorWindows, detect the editor skin to auto-select:

```csharp
string themeClass = EditorGUIUtility.isProSkin
    ? "bar-graph--dark"
    : "bar-graph--light";
graph.AddToClassList(themeClass);
```

---

## Part 2: Color Blind Accessibility — Chrome

### Approach

Three USS class selectors for common color vision deficiencies, applied alongside the light/dark mode class:

- `.bar-graph--cvd-deuteranopia` — red-green (most common, ~5% of males)
- `.bar-graph--cvd-protanopia` — red-green (less common, ~2.5% of males)
- `.bar-graph--cvd-tritanopia` — blue-yellow (rare, ~0.01%)

These override only the chrome colors that could be confused. Most chrome is already safe — blue selection, white/black hover tints — because the defaults avoid red-green as the primary distinction.

**Important:** CVD selectors must appear AFTER theme selectors in the USS file so they win the cascade (both are single-class selectors with equal specificity — declaration order breaks ties).

### What needs to change for color blind chrome

The main risk areas are:
- **Selection rim vs focus rim** — currently blue vs yellow. Safe for deuteranopia/protanopia. Problematic for tritanopia (blue-yellow confusion).
- **Hover tint** — white/black overlay. Safe for all types.
- **Grid and axis** — neutral grays. Safe for all types.

Tritanopia override:

```css
.bar-graph--cvd-tritanopia {
    /* Replace blue selection with magenta — distinguishable from yellow */
    --bar-graph-selection-rim-color:     rgba(200, 80, 200, 0.9);
    --bar-graph-segment-selection-color: rgb(255, 150, 50);
    /* Replace yellow focus with orange — distinguishable from magenta */
    --bar-graph-focus-rim-color:         rgb(255, 150, 50);
}
```

Deuteranopia/protanopia typically don't need chrome overrides because the defaults already avoid red-green pairings. But for completeness:

```css
.bar-graph--cvd-deuteranopia,
.bar-graph--cvd-protanopia {
    /* Ensure selection and focus remain distinct under red-green deficiency.
       Default blue/yellow is already safe, but strengthen the contrast. */
    --bar-graph-selection-rim-color:     rgba(0, 114, 178, 0.9);
    --bar-graph-focus-rim-color:         rgb(230, 159, 0);
    --bar-graph-segment-selection-color: rgb(230, 159, 0);
}
```

These RGB values are from the Okabe-Ito palette, which is specifically designed and validated for color vision deficiency.

### Combining modes

Classes compose. A dark-mode tritanopia graph:

```csharp
graph.AddToClassList("bar-graph--dark");
graph.AddToClassList("bar-graph--cvd-tritanopia");
```

USS cascade handles this correctly because CVD selectors appear after theme selectors in the file — they override only the properties they set, inheriting everything else from the theme.

---

## Part 3: Color Blind Accessibility — Data Colors

### The problem

Data colors are set by the consumer via `new BarSegment(value, color, tag)`. The graph element doesn't control them. A consumer using red and green for two allocation sites will be indistinguishable for deuteranopia users regardless of what the graph's USS says.

### Recommended palettes

The graph package should ship recommended palette constants that consumers can use. Based on the Okabe-Ito palette (validated for all CVD types):

#### 8-Color Categorical Palette (Okabe-Ito)

| Index | Name | RGB | Hex | Notes |
|-------|------|-----|-----|-------|
| 0 | Blue | (0, 114, 178) | `#0072B2` | Safe anchor color for all CVD types |
| 1 | Orange | (230, 159, 0) | `#E69F00` | Warm contrast to blue |
| 2 | Sky Blue | (86, 180, 233) | `#56B4E9` | Lighter blue, distinct from index 0 |
| 3 | Bluish Green | (0, 158, 115) | `#009E73` | Safe green (blue-shifted, avoids red-green confusion) |
| 4 | Yellow | (240, 228, 66) | `#F0E442` | High lightness, use on dark backgrounds |
| 5 | Vermillion | (213, 94, 0) | `#D55E00` | Red-shifted orange, distinct from true red |
| 6 | Reddish Purple | (204, 121, 167) | `#CC79A7` | Pink-purple, avoids red-green axis |
| 7 | Black/Dark Gray | (51, 51, 51) | `#333333` | Neutral anchor |

Ship as a static utility:

```csharp
namespace GCAllocBreakdown.BarChart.Core
{
    /// <summary>
    /// Color-blind-safe categorical palettes for bar/segment data colors.
    /// Based on the Okabe-Ito palette, validated for deuteranopia, protanopia,
    /// and tritanopia.
    /// </summary>
    public static class BarGraphPalettes
    {
        /// <summary>
        /// 8-color Okabe-Ito palette. Safe for all common CVD types.
        /// Use modulo indexing for more than 8 categories:
        /// <c>OkabeIto[categoryIndex % OkabeIto.Length]</c>
        /// </summary>
        public static readonly Color32[] OkabeIto = new Color32[]
        {
            new(  0, 114, 178, 255),  // Blue
            new(230, 159,   0, 255),  // Orange
            new( 86, 180, 233, 255),  // Sky Blue
            new(  0, 158, 115, 255),  // Bluish Green
            new(240, 228,  66, 255),  // Yellow
            new(213,  94,   0, 255),  // Vermillion
            new(204, 121, 167, 255),  // Reddish Purple
            new( 51,  51,  51, 255),  // Dark Gray
        };

        /// <summary>
        /// 6-color blue-orange diverging palette.
        /// Good for stacked bars where you want warm/cool distinction.
        /// Safe for deuteranopia and protanopia.
        /// </summary>
        public static readonly Color32[] BlueOrange6 = new Color32[]
        {
            new( 30,  90, 170, 255),  // Dark Blue
            new( 80, 150, 220, 255),  // Medium Blue
            new(160, 200, 240, 255),  // Light Blue
            new(255, 200, 120, 255),  // Light Orange
            new(240, 150,  40, 255),  // Medium Orange
            new(200,  90,   0, 255),  // Dark Orange
        };
    }
}
```

### Consumer usage

```csharp
// Building segments with color-blind-safe colors
var palette = BarGraphPalettes.OkabeIto;
for (int s = 0; s < siteCount; s++)
{
    segs[cursor] = new BarSegment(
        value,
        palette[s % palette.Length],
        tag: s
    );
}
```

### Runtime palette remapping via BarVisualProvider

`BarVisualProvider` overrides at the **bar level** — its `Color` property applies uniformly to ALL segments within a bar. This makes it suitable for single-segment (non-stacked) bars but **not for stacked bars with per-segment colors**.

For non-stacked bars (one segment per bar):

```csharp
// Color blind mode toggle — works for non-stacked bars only
Color32[] remapPalette = BarGraphPalettes.OkabeIto;
bool colorBlindMode = false;

graph.BarVisualProvider = colorBlindMode
    ? (int dataIdx) =>
    {
        return new BarVisualOverride
        {
            Color = remapPalette[dataIdx % remapPalette.Length]
        };
    }
    : null;
```

For stacked bars (multiple segments per bar), the consumer must rebuild the segment data with safe palette colors — `BarVisualProvider` cannot remap individual segments. A future `SegmentVisualProvider` could address this (see Future Considerations).

---

## Part 4: Segment vs Bar Color Handling

### How colors flow

```
Consumer sets data:
  BarSegment(value, color, tag)
       ↓
Graph stores in ChartDataModel:
  _segments[i].Color
       ↓
DrawDirectBars reads per segment:
  ResolveSegmentColor(seg.Color, alpha)
       ↓
  If seg.Color == default(Color32):
    Use _vis.DefaultBarColor (from USS --bar-graph-default-bar-color)
  Else:
    Use seg.Color as-is
       ↓
  Apply alpha (dim opacity, BarVisualProvider override)
       ↓
  Emit to quad buffer
```

### What USS controls

- `--bar-graph-default-bar-color` — the fallback when a segment has `default(Color32)`. This changes with light/dark mode (darker blue for light mode, brighter blue for dark mode).
- All chrome colors (selection, hover, focus, grid, axes, drag rect, overlay tint, tag highlight)

### What USS does NOT control

- Explicit segment colors set by the consumer (`new BarSegment(100, Color.red)`)
- Per-bar overrides from `BarVisualProvider`

### Light mode data color considerations

Consumer-chosen data colors designed for dark backgrounds may look bad on light backgrounds. The graph can't fix this — it's the consumer's responsibility. Recommendations:

- If supporting both modes, use medium-saturation colors that work on both backgrounds
- Avoid very light colors (invisible on light bg) or very dark colors (invisible on dark bg)
- The Okabe-Ito palette works well on both light and dark backgrounds
- Use `--bar-graph-default-bar-color` in USS to set a mode-appropriate fallback for bars without explicit colors

---

## Files

| File | Type | Description |
|------|------|-------------|
| `Editor/BarChart/Resources/BarGraph.uss` | MODIFY | Add `--bar-graph-segment-selection-color` to base `.bar-graph`, add `.bar-graph--dark`, `.bar-graph--light`, CVD class selectors |
| `Editor/BarChart/Core/BarGraphPalettes.cs` | NEW | Static color-blind-safe palette constants (~40 lines) |

No changes to `BarGraphElement.cs`, `ChartViewState.cs`, `BarGraphSettings.cs`, or any handler.

**Note:** `--bar-graph-segment-selection-color` is consumed in C# with a fallback to `--bar-graph-focus-rim-color`, but is not currently defined in USS. Add it to the base `.bar-graph` selector so the default is explicit rather than relying on the C# fallback.

---

## Implementation Order

1. **Add missing property to base selector** — add `--bar-graph-segment-selection-color` to `.bar-graph`
2. **Dark/light USS classes** — add to `BarGraph.uss`, test with existing demos
3. **CVD chrome overrides** — add to `BarGraph.uss` AFTER theme selectors, test by visual inspection under simulated CVD
4. **`BarGraphPalettes`** — new static class with Okabe-Ito and BlueOrange6
5. **Demo update** — add a toggle button for light/dark mode and a dropdown for CVD mode in the gallery demo
6. **Documentation** — usage examples for consumers: how to apply modes, how to use palettes, how to remap with `BarVisualProvider`

---

## Verification

1. Dark mode — graph looks identical to current default (values match `.bar-graph` base selector exactly)
2. Light mode — readable chrome, sufficient label contrast (WCAG AA 4.5:1), no invisible elements
3. Toggle between modes — all chrome updates, no stale colors, no flicker
4. CVD deuteranopia chrome — selection rim and focus rim are distinguishable under simulated deuteranopia
5. CVD tritanopia chrome — selection rim and focus rim are distinguishable under simulated tritanopia
6. Okabe-Ito palette — all 8 colors are distinguishable under simulated deuteranopia, protanopia, and tritanopia
7. `BarVisualProvider` color remap — existing data renders with remapped palette, no data mutation
8. Light mode + explicit dark data colors — consumer data renders correctly (even if aesthetically imperfect)
9. EditorWindow auto-detection — graph picks dark/light based on Unity editor skin
10. Class composition — `.bar-graph--light.bar-graph--cvd-tritanopia` applies both correctly

---

## Future Considerations

- **`SegmentVisualProvider`** — per-segment color override at render time, enabling CVD remapping for stacked bars without rebuilding data. Adds one callback per visible segment to the render loop.
- **High contrast mode** — a USS class that maximizes contrast for low-vision users (thicker lines, larger grid, bolder chrome). Pure USS, no code changes.
- **Pattern fills** — hatch patterns or texture overlays on segments as an additional visual channel beyond color. Would require shader changes or Painter2D pattern support.
