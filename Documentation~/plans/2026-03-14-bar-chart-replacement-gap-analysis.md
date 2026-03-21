# Gap Analysis: Replacing GCAllocAnalyzer Bar View with ui-toolkit-extensions BarChart

## Context

**Goal**: Replace the custom bar graph in GCAllocAnalyzer (`GraphElement.cs` + `PerFrameGraphController.cs`) with the reusable `BarGraphElement` from `ui-toolkit-extensions`.

**Why**: The ui-toolkit-extensions bar chart is a standalone, well-architected UPM package with zero-alloc rendering, pluggable input, LOD, color batching, and a clean public API. Extracting and reusing it avoids maintaining two parallel Painter2D bar renderers.

---

## Feature Comparison

| Feature | GCAllocAnalyzer (current) | ui-toolkit-extensions | Gap? |
|---------|--------------------------|----------------------|------|
| Painter2D rendering | Yes | Yes (color-batched) | No |
| Zoom X (scroll wheel) | Yes | Yes (cursor-anchored) | No |
| Zoom Y (Ctrl+scroll) | Yes | Yes | No |
| Pan X/Y (middle-drag) | Yes | Yes (middle-click + Alt+drag) | No |
| WASD zoom/pan | Yes | No (arrows for selection nav) | **YES** |
| Rubber-band drag select | Yes | Yes | No |
| Click select | Yes | Yes (+ Ctrl+click additive) | No |
| Keyboard nav (arrows) | Yes (arrow keys move frame) | Yes (arrows + Shift + Home/End) | No |
| Sort by magnitude | Yes (toggle) | Yes (ByValue asc/desc) | No |
| Stacked segments | Yes (1:1 zoom only, method-colored) | Yes (BarSegment/BarEntry) | No |
| Grid lines + Y labels | Yes (dynamic step, byte format) | Yes (pluggable formatter) | No |
| Hover highlight | Yes (white 12% overlay) | Yes (event-based) | No |
| LOD (sub-pixel bars) | No (uses bucketing) | Yes (pixel-column merge) | N/A |
| **Overview strip** | Yes (minimap + draggable viewport) | **No** | **YES** |
| **Adaptive bucketing** | Yes (multi-frame → 1 bar when zoomed out) | No (LOD visual-only, no data aggregation) | **MAYBE** |
| **Analyzed-range dimming** | Yes (50% opacity for unanalyzed) | No (uniform bar colors) | **YES** |
| **Partial-height brightness** | Yes (bright caps at selected-frame max) | No | **YES** |
| **Segment overlay (method highlight)** | Yes (white tint on highlighted method) | Has overlay dataset, not per-segment tint | **YES** |
| **Y-axis drag scaling** | Yes (exponential drag → custom max + Y pan) | No | **YES** |
| **Context menu** | Yes (Select All, Max GC, Analyze, etc.) | No (not part of bar chart) | **YES** |
| **Tooltip** | Yes (frame range, bytes, segment details) | No (fires HoverChanged, no built-in tooltip) | **YES** |
| **Frame-index selection persistence** | Yes (selection stored as frame indices) | Selection by bar index | **ADAPTER** |

---

## Gap Categories

### 1. NOT NEEDED — LOD vs Bucketing (Architectural Difference)

The current analyzer **aggregates data** into buckets when zoomed out (max value per bucket). ui-toolkit-extensions uses **visual LOD** (pixel-column merge when slot < 1px).

**These are different strategies:**
- Bucketing: fewer bars in data model, each represents a frame range
- LOD: all bars in data model, renderer merges visually

**Assessment**: LOD may be *sufficient* if we pass all frames as individual bars and let the renderer handle density. The LOD renderer picks the max value per pixel column — functionally similar to max-per-bucket. The question is whether 10K+ bars with LOD performs acceptably vs pre-bucketed 500 bars. Given the color-batching optimization, likely fine. **Test this first.**

If LOD is insufficient, we'd need to add bucketing to ChartDataModel or keep a thin adapter that pre-buckets before calling SetData().

### 2. CRITICAL GAPS — Must Be Built

#### 2a. Overview Strip
The minimap showing full data range with a draggable viewport rectangle is not in ui-toolkit-extensions. Options:
- **A**: Add a second BarGraphElement configured as a mini overview (read-only, no interaction except viewport drag). Wire its ViewChanged events to the main graph.
- **B**: Build a lightweight OverviewStripElement in ui-toolkit-extensions that renders a simplified version.
- **C**: Keep the overview strip as a separate custom element in GCAllocAnalyzer.

**Recommendation**: Option A — a second BarGraphElement with interaction disabled except for a custom viewport-drag handler. This reuses existing rendering. The viewport rectangle overlay would need to be added to BarGraphElement as a feature.

#### 2b. Analyzed-Range Dimming
Bars outside the analyzed sub-range need to appear dimmed. ui-toolkit-extensions doesn't have per-bar opacity.

**Options:**
- Set bar colors directly: analyzed bars get full teal, unanalyzed bars get dimmed teal. This works with the existing API since SetData accepts per-bar colors via BarEntry/BarSegment.
- Add a "mask" or "dim range" feature to BarGraphElement.

**Recommendation**: Use per-bar colors. The controller already knows which frames are analyzed — just set Color32 accordingly when building BarEntry arrays. No changes to ui-toolkit-extensions needed.

#### 2c. Partial-Height Brightness
When a bar spans both selected and unselected frames, the bright portion caps at the max GC of only selected frames. This is a visual split within a single bar.

**Options:**
- Model as a 2-segment stacked bar: bottom segment (bright, up to selected max), top segment (dimmed, remainder). This maps naturally to BarSegment.
- Add explicit partial-brightness rendering to BarGraphElement.

**Recommendation**: Use stacked segments — it's already supported. Build each bar as 2 segments when it has mixed selection state.

#### 2d. Segment Overlay (Method Highlighting)
When a marker is selected, its segments across all frames get a tint overlay. ui-toolkit-extensions has an overlay dataset but it's a separate series of values, not a per-segment tint.

**Options:**
- Use the overlay system: create an overlay that highlights only the selected method's contribution per frame.
- Modify bar colors to emphasize the selected method's segments and dim others.
- Add a segment-level highlight feature to BarGraphElement.

**Recommendation**: Modify bar segment colors when a method is selected: keep the selected method's segments at full color, dim all other segments. Rebuild BarEntry data when selection changes. No changes to ui-toolkit-extensions needed.

#### 2e. Y-Axis Drag Scaling
Manual Y-axis adjustment via drag doesn't exist. This requires:
- A drag zone along the Y-axis area
- Exponential mapping (drag distance → scale factor)
- Y panning when custom-scaled

**Recommendation**: Add a new input handler (`BarGraphYScaleHandler`) to ui-toolkit-extensions that detects drags in the left padding area and adjusts ViewState.ZoomY + PanY.

#### 2f. WASD Keys
Current analyzer uses W/S for zoom, A/D for pan. ui-toolkit-extensions uses arrows for selection navigation.

**Recommendation**: Add a `BarGraphKeyboardNavHandler` (or extend existing) that maps WASD to zoom/pan. This is a simple addition to the input handler system.

### 3. WRAPPER/ADAPTER GAPS — Built in GCAllocAnalyzer

#### 3a. Context Menu
Not part of the bar chart (nor should it be — it's application-specific).

**Recommendation**: Add via `ContextualMenuManipulator` in the GCAllocAnalyzer controller, same as current approach. No changes to ui-toolkit-extensions.

#### 3b. Tooltip
ui-toolkit-extensions fires `HoverChanged` events. The analyzer needs to show frame-specific info.

**Recommendation**: Listen to HoverChanged, create/position a tooltip VisualElement in the analyzer controller. Map bar index back to frame data for content. No changes to ui-toolkit-extensions.

#### 3c. Frame-Index Selection Mapping
ui-toolkit-extensions selection is bar-index-based. The analyzer needs frame-index-based selection that survives zoom changes.

**Recommendation**: Build a mapping layer in the analyzer controller: bar index ↔ frame index. On SelectionChanged, translate to frame indices. On zoom change, translate frame indices back to bar indices and re-apply. This is adapter logic, not a bar chart feature.

#### 3d. Magnitude-Mode Selection Persistence
In sorted mode, selection is stored as value range (min/max bytes) not position. This survives re-bucketing.

**Recommendation**: Same adapter approach — store value range in the analyzer controller, re-derive bar selection after sort/zoom changes. No changes to ui-toolkit-extensions.

---

## Summary of Required Changes

### Changes TO ui-toolkit-extensions:
1. **WASD key handler** — new input handler or extend keyboard handler
2. **Y-axis drag scaling** — new input handler for left-padding drag
3. **Viewport overlay rectangle** — for overview strip (optional; could be done externally)

### Changes IN GCAllocAnalyzer (adapter/controller):
1. **Overview strip** — second BarGraphElement instance + viewport sync
2. **Analyzed-range dimming** — set per-bar colors based on analyzed state
3. **Partial-height brightness** — model as 2-segment stacked bars
4. **Method highlight** — modify segment colors when marker selected
5. **Context menu** — ContextualMenuManipulator on the element
6. **Tooltip** — listen to HoverChanged, show custom tooltip element
7. **Frame-index mapping** — bar index ↔ frame index adapter
8. **Magnitude-mode selection** — value-range persistence in controller

### Nothing needed (already works):
- Zoom/Pan (scroll wheel, middle-drag)
- Rubber-band selection
- Click selection
- Sort by magnitude (ByValue mode)
- Stacked segments (BarSegment/BarEntry)
- Grid lines (pluggable byte formatter)
- Hover highlight
- LOD rendering for large frame counts

---

## Verification Plan
1. **Performance test**: Pass 10K+ individual frame bars to BarGraphElement, verify LOD rendering performance is acceptable (compare to current bucketed approach)
2. **Visual parity**: Screenshot current analyzer graph, replicate same data through ui-toolkit-extensions, compare visually
3. **Selection round-trip**: Verify drag-select → zoom → selection preserved via frame-index adapter
4. **Segment rendering**: Verify stacked segments at 1:1 zoom match current method-colored display
5. **Dimming**: Verify per-bar color approach produces same analyzed/unanalyzed visual distinction
