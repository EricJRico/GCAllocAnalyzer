# Task 4: Per-Frame Allocation Bar Graph — Design

## Goal

A Profile-Analyzer-style horizontal bar chart at the top of the left panel showing total GC allocation bytes per frame, with spike visualization and selected-group overlay.

## Visual Reference

Matches the Profile Analyzer's frame time graph: vertical bars spanning the panel width, Y-axis scale on the left, X-axis frame labels on the bottom, horizontal guide lines, and a selected marker overlay in a contrasting color.

```
┌─ Per-Frame Graph ───────────────────────────────────────────────┐
│ 48.2KB ┊·····················································  │
│        ┊                  ██                                    │
│        ┊                  ██        ██                          │
│ 24.1KB ┊··············██··██···██···██·························  │
│        ┊         ██   ██  ██   ██   ██   ██                    │
│        ┊    ██   ██   ██  ██   ██   ██   ██   ██   ██         │
│        ┊   ▓██   ██   ██ ▓██  ▓██  ▓██   ██  ▓██   ██   ██   │
│  0     ┊   ▓██  ▓██  ▓██ ▓██  ▓██  ▓██  ▓██  ▓██  ▓██  ▓██   │
│        100       200   [median]      400       500         600 │
│                                          SomeMethod.GCAlloc    │
└─────────────────────────────────────────────────────────────────┘
  █ = total per frame (teal → yellow → red by statistical threshold)
  ▓ = selected group's contribution (brighter overlay)
```

## Rendering Approach: VisualElement Bars + Bucketing

Each bar is a `VisualElement` with an inner child for the group overlay. When frame count exceeds pixel width, adjacent frames are bucketed (value = max of bucket). This caps element count at the container's pixel width.

### Why not MeshGenerationContext?

- VisualElements give free tooltips, hover, click handling per-bar
- Bucketing ensures element count never exceeds pixel width (~600-800 max)
- Simpler to maintain, debug (bars visible in UI Toolkit Debugger)
- MeshGenerationContext would save ~0ms for our frame counts but costs ~100 extra lines of manual hit-testing

## Architecture

### New UI Fields

```csharp
Foldout m_GraphFoldout;
VisualElement m_GraphContainer;       // outer container with Y-axis + chart area
VisualElement m_GraphBarArea;         // the bars live here (dark background)
Label m_GraphYMax, m_GraphYMid;       // Y-axis scale labels
Label m_GraphXStart, m_GraphXEnd;     // X-axis frame labels
Label m_GraphOverlayLabel;            // selected marker name (bottom-right)
```

### New Data

```csharp
long[] m_GraphBucketValues;           // bucketed total bytes (reused buffer)
long[] m_GraphOverlayValues;          // bucketed selected-group bytes
int m_GraphBucketCount;               // current number of buckets
int m_GraphFramesPerBucket;           // frames represented per bucket
```

### Placement in BuildLeftPanel()

Between `m_FiltersFoldout` and `m_MarkerHeaderRow`:

```
left.Add(m_FiltersFoldout);
left.Add(BuildPerFrameGraph());    // NEW
left.Add(m_MarkerHeaderRow);
```

### BuildPerFrameGraph()

Returns a `Foldout` ("Per-Frame Graph") containing:
1. Y-axis label column (left, ~50px): max and mid labels, bottom-anchored "0"
2. Chart area (fills remaining width):
   - Dark background (`#1A1A1A`)
   - Horizontal dotted guide lines at max and mid Y values
   - Bar container: bars anchored to bottom, flex-direction row, align-items flex-end
   - X-axis label row (frame start, median position marker, frame end)
   - Overlay label (bottom-right, showing selected group name)

Height: 120px for the graph area, plus ~18px for X-axis labels = ~138px total foldout content.

Register `GeometryChangedEvent` on the chart area to trigger re-bucketing when width changes.

### RebuildGraph()

Called after analysis, load, or container resize.

1. Read `m_Snapshot.PerFrameBytes` (already computed)
2. Calculate `bucketCount = min(frameCount, floor(chartAreaWidth))`
3. Calculate `framesPerBucket = ceil(frameCount / bucketCount)`
4. For each bucket: value = max of frames in that bucket
5. Find `maxValue` across all buckets
6. Compute `mean` and `stddev` for color thresholds
7. Clear existing bar children from `m_GraphBarArea`
8. For each bucket, create a bar `VisualElement`:
   - Width: `chartAreaWidth / bucketCount`
   - Height: `(value / maxValue) * graphHeight`
   - BackgroundColor: teal (≤mean+1σ), yellow (>mean+1σ), red (>mean+2σ)
   - Inner child element for overlay (initially hidden, height 0)
   - Tooltip: "Frame N: X KB" or "Frames N-M: X KB (max)"
   - Click handler → set Profiler frame index
9. Update Y-axis labels: "0", FormatBytes(maxValue/2), FormatBytes(maxValue)
10. Update X-axis labels: frame start, frame end

### UpdateGraphOverlay(CallsiteGroup group)

Called when marker selection changes.

1. If group is null: hide all inner overlay elements, clear overlay label
2. Build per-frame bytes for the group from `group.Allocations` (bucket same as main graph)
3. For each bar element, set inner overlay element height proportional to group's bucket value
4. Set overlay label text to group's DisplayName

### Color Scheme

| Condition | Color | Hex |
|---|---|---|
| ≤ mean + 1σ | Teal | `#44AA99` |
| > mean + 1σ | Yellow | `#FFCC33` |
| > mean + 2σ | Red | `#FF4444` |
| Overlay (selected group) | Light cyan | `#AADDCC` |
| Guide lines | Subtle gray | `#333333` |

### Interaction

- **Tooltip**: Native `tooltip` property on each bar element
- **Click**: Jump to frame in Profiler. For bucketed bars, jump to the frame with max allocation in the bucket
- **Click implementation**: `m_ProfilerWindow.selectedFrameIndex = frameIndex` (lighter than full `SelectInCpuModule` — we don't need sample-level selection for the graph)

### Rebuild Triggers

- `RunAnalysis()` completion → `RebuildGraph()`
- `LoadSnapshot()` → `RebuildGraph()`
- `TryRestoreAfterReload()` → `RebuildGraph()`
- `GeometryChangedEvent` on bar area → `RebuildGraph()` (debounced)
- `OnMarkerSelectionChanged()` → `UpdateGraphOverlay(selectedGroup)`

## Scope Boundaries

**In scope (Task 4):**
- Bar chart with bucketing
- Statistical color coding
- Y-axis scale + guide lines
- X-axis frame labels
- Click-to-frame
- Tooltips
- Selected group overlay
- Collapsible foldout

**Out of scope (Task 12):**
- Drag-select to define a sub-range
- Right-click context menu on graph
- Budget lines
