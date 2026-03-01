# Graph Architecture Redesign — Full-Range View with Cached Analysis

**Date:** 2026-03-01
**Scope:** Replaces current Task 7 graph behavior with a properly architected full-range graph, cached analysis, and auto-analyze on drag-select.

---

## Problem Statement

The current graph implementation conflates three distinct concepts:
1. **Analyzed range** — the frames that have full call-stack-resolved data
2. **Displayed range** — what the graph shows (currently == analyzed range)
3. **Visual selection** — the highlighted bars from drag-select

This causes a cascade of bugs:
- Frame count mismatch after "Analyze Selection" (stale bar indices)
- Ghost selections appearing outside the selected area
- Graph breaking after "Select All + Analyze" following a sub-range analyze
- No visual indication of what was actually analyzed vs. what is selected

---

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Graph scope | Full range + highlight | Graph always shows all profiler frames. Analyzed sub-range indicated by dimming. Eliminates "what did I analyze?" confusion. |
| Drag-select behavior | Auto-analyze on drag-complete | Most user-friendly — no need to discover context menu. Drag-select → instant re-analysis. |
| Analyzed range indicator | Dim un-analyzed bars (~30% opacity) | Clear at a glance without adding visual clutter. |
| Sub-range analysis | Filter from cache (no re-extraction) | First Analyze caches all RawAllocations. Sub-range = filter + regroup in memory. Near-instant. |
| Reset to full range | Inline button on graph | Appears only when viewing a sub-range. Click restores cached full snapshot. Instant. Clears selection. |
| Order by Size scope | Sort all frames | All bars participate in sort. Dimming still shows which are analyzed. |
| Single click behavior | Select bar at X position | Position-based hit testing. Highlights bar, jumps to frame in Profiler. |
| Full analysis cache | Cache after first Analyze | Invalidated only by new Pull Data or Load. Reset restores from cache instantly. |

---

## Data Architecture

### New: `GraphFrameStore`

Holds the full-range graph data and the cached analysis, separate from the current-view snapshot.

```
Fields:
  int FullFrameStart
  int FullFrameEnd
  long[] FullFrameBytes              // per-frame GC totals for entire profiler range
  List<RawAllocation> CachedRawAllocations   // full extraction, cached after first Analyze
  List<string> CachedSortedThreadNames       // thread names from full analysis
  bool HasFullFrameData              // true after Pull Data
  bool HasCachedAnalysis             // true after first Analyze
```

### Existing: `AnalysisSnapshot` (unchanged)

Represents the **current view** — may be full range or a sub-range. Contains grouped/filtered data that feeds the table, marker summary, top offenders, and overlay.

### Relationship

- `GraphFrameStore` is the **source of truth** for the graph and for re-slicing.
- `AnalysisSnapshot` is the **derived view** rebuilt from cached data on each range change.
- The graph controller receives both: `GraphFrameStore` for bar rendering, `AnalysisSnapshot` for knowing the analyzed range and computing overlays.

---

## Graph State Model

Three independent pieces of state that never conflate:

### 1. Frame Data (immutable after Pull Data)
- `GraphFrameStore.FullFrameBytes` — one entry per profiler frame
- Set once by Pull Data (fast scan, no call stacks)
- Never changes until next Pull Data

### 2. Analyzed Range (changes on each analysis/slice)
- `AnalysisSnapshot.FrameStart` / `FrameEnd`
- Bars inside render at full brightness; bars outside render at ~30% opacity
- Initially equals full range. Narrows on drag-select. Resets to full on Reset.

### 3. Visual Selection (transient, user-driven)
- `m_SelectionStartBar` / `m_SelectionEndBar` — display-index based
- Purely visual highlight
- **Cleared automatically** when analysis completes (selection "became" the analyzed range)

---

## Interaction Flow

### Pull Data
1. Fast scan: iterate all profiler frames, sum `GC.Alloc` sizes per frame (no call stack resolution)
2. Populate `GraphFrameStore.FullFrameBytes`
3. Graph renders all bars **dimmed** (nothing analyzed yet)
4. Table is empty, no snapshot data

### First Analyze
1. Full Profiler extraction on the toolbar frame range (expensive — call stack resolution)
2. Populate `m_Snapshot` with groupings, stats, per-frame bytes
3. Cache `RawAllocations` and thread names into `GraphFrameStore`
4. Graph brightens analyzed bars, table populates

### Drag-Select (auto-analyze)
1. User drags to select a range of bars
2. On mouse-up: determine frame range from selected bars
3. Filter `GraphFrameStore.CachedRawAllocations` by frame range
4. Rebuild groupings/stats into `m_Snapshot` (cheap, in-memory)
5. Graph updates dimming, table refreshes
6. Visual selection clears
7. **No Profiler access** — instant

### Single Click
1. Hit-test bar at X position
2. Highlight that bar (yellow)
3. Jump to that frame in Unity Profiler window

### Keyboard Navigation
1. Arrow keys move/resize visual selection
2. Selection is purely visual — does NOT auto-analyze
3. Enter key or context menu "Analyze Selection" triggers analysis on selected range

### Reset (inline graph button)
1. Only visible when analyzed range != full range
2. Click: filter `CachedRawAllocations` using full range (no filter) → rebuild groupings
3. All bars bright, selection cleared, table shows full data
4. **Instant** — no Profiler access

### New Pull Data
1. Clears everything: `GraphFrameStore`, `m_Snapshot`, cache
2. Fresh start — re-scans profiler frames

---

## Rendering Details

### Bar Colors

| Bar State | Condition | Visual |
|-----------|-----------|--------|
| Full brightness | Frame within analyzed range | `k_BarNormal` (teal) |
| Dimmed | Frame outside analyzed range | Same teal at ~30% opacity |
| Selected | Bar within active drag-selection | `k_BarSelected` (lighter teal) |
| Highlighted | Single-clicked bar | `k_BarHighlighted` (yellow) |

### Draw Order (unchanged)
1. Background fill
2. Grid lines
3. Selection background band
4. Bars (with dim/bright/selected/highlighted states)
5. Overlay bars (white, only on analyzed-range bars)

### Order by Size Mode
- All bars (full range) sorted by magnitude descending
- Dimming still applies — analyzed bars are bright, un-analyzed are dim
- Overlay only draws on analyzed bars

### Reset Button
- Small clickable label, positioned in a corner of the graph area
- Only visible when `AnalysisSnapshot.FrameStart/End != GraphFrameStore.FullFrameStart/End`
- Click triggers full-range restore from cache

### X-Axis Labels
- Always show full range start/end frame numbers
- In sub-range mode, could optionally show analyzed range markers

---

## Code Changes Summary

### New File/Class
- `GraphFrameStore` — data class (could be a nested class in the window or a standalone file)

### Modified: `GCAllocAnalyzerWindow.cs`
- New field: `GraphFrameStore m_FrameStore`
- `OnPullData()` → performs fast scan into `m_FrameStore.FullFrameBytes`
- `RunAnalysis()` → after extraction, caches into `m_FrameStore`
- New method: `RebuildFromCache(int startFrame, int endFrame)` — filters cached allocations, rebuilds groupings/stats/UI
- Drag-complete handler calls `RebuildFromCache()` instead of `OnAnalyze()`
- Reset handler calls `RebuildFromCache(m_FrameStore.FullFrameStart, m_FrameStore.FullFrameEnd)`

### Modified: `PerFrameGraphController.cs`
- `SetData()` takes `GraphFrameStore` (for bars) + analyzed range (for dimming)
- `RebuildGraph()` reads from `FullFrameBytes` instead of `m_Snapshot.PerFrameBytes`
- New field: analyzed start/end bar indices for dim/bright rendering
- Reset button UI element + visibility logic
- Overlay computation uses `m_Snapshot.PerFrameBytes` scoped to analyzed range only

### Modified: `GraphElement.cs`
- New dim color constant (`k_BarDimmed`)
- `SetAnalyzedRange(int startBar, int endBar)` — tells the element which bars are bright
- `DrawBars()` checks each bar against analyzed range for color selection
- Overlay skips bars outside analyzed range

### Unchanged
- `RunAnalysis()` extraction logic (same code, just called once)
- `BuildGrouping()`, `ApplyFilters()`, `BuildTopOffenders()` — same logic
- `GraphElement` draw order and pipeline structure
- Keyboard navigation logic (operates on visual selection)
- Context menu structure (just rewire actions)

---

## What This Fixes

1. **Frame count mismatch** — graph always shows full range; analyzed range is clearly indicated by dimming
2. **Ghost selections** — selection is cleared on analysis; bar indices stay valid because bar count doesn't change (always full range)
3. **Select All + Analyze breaks** — bar count is stable; "select all" selects all full-range bars; analyze re-slices from cache
4. **No analyzed-range indicator** — dimming makes it immediately obvious what was analyzed

---

## Zoom & Navigation (Perfetto-Style Two-Level Layout)

### Overview

Inspired by Perfetto's trace viewer, the graph area uses a two-level layout:

1. **Overview strip** (~20–30px tall) — always shows the entire capture range in miniature
2. **Detail view** (main graph, ~120px tall) — zoomable/scrollable view where all interaction happens

This replaces the original "single graph with dimming" approach. Both levels still use dimming for the analyzed range, but the overview strip provides constant context when the detail view is zoomed in.

### Overview Strip

- Renders a miniature version of all bars (full capture range, always visible)
- A **viewport rectangle** (semi-transparent overlay) shows which portion of the full range is currently visible in the detail view below
- Analyzed range shown by dimming (same as detail view)
- **Drag the viewport rectangle** to pan the detail view
- Clicking outside the viewport rectangle jumps the viewport to that position
- The strip is non-interactive for selection — selection only happens in the detail view

### Detail View

- The main graph with full interactivity (drag-select, click, keyboard selection, context menu)
- Displays a subset of the full bar range based on the current zoom/scroll state
- Bars render wider when zoomed in (more pixels per frame)
- Horizontal `Scroller` appears below the detail view when zoomed in; hidden at full zoom-out
- All existing rendering applies: dimming, selection highlight, overlay, grid lines

### Viewport State

```
float m_ViewportStart  // 0.0 = beginning of capture
float m_ViewportEnd    // 1.0 = end of capture
```

- Full zoom-out: `m_ViewportStart = 0.0`, `m_ViewportEnd = 1.0` (overview strip hidden or viewport rect covers entire strip)
- Zoomed in: narrower window, e.g. `0.3` to `0.5`
- Bar width in detail view = `areaWidth / visibleBarCount`
- Only bars within the viewport range are drawn (performance)

### Navigation Controls (WASD)

| Key | Action |
|-----|--------|
| **W** | Zoom in (centered on viewport center) |
| **S** | Zoom out (toward full range) |
| **A** | Pan left |
| **D** | Pan right |
| **Mouse wheel** | Zoom in/out (centered on cursor X) |

- WASD requires the graph element to have focus (click to focus)
- Zoom has a minimum limit (e.g. 10 bars visible) and maximum (full range)
- Pan clamps to `[0.0, 1.0]` bounds

### Interaction with Existing Features

- **Drag-select** — works within the visible viewport in the detail view. Selected bar indices are viewport-relative, resolved to absolute indices for analysis.
- **Order by Size** — zoom/scroll applies to the sorted bar order. Overview strip shows sorted miniature.
- **Overlay** — drawn only on visible, analyzed-range bars in the detail view. Overview strip does not show overlay (too small to be useful).
- **Reset button** — still inline on the detail view. Resets analyzed range AND optionally resets zoom to full.

### Complexity Assessment

This adds moderate complexity on top of the base redesign:

- **Overview strip** — a second `GraphElement` instance (or a simpler custom element) that draws miniature bars + viewport rect. ~100 lines.
- **Viewport state + WASD/scroll handlers** — ~60 lines in the controller.
- **Detail view bar clipping** — only draw bars within viewport range. ~20 lines adjustment to existing draw loop.
- **Scroller integration** — UIElements `Scroller` wired to viewport state. ~30 lines.
- **Hit testing adjustment** — account for viewport offset when resolving bar indices. ~10 lines.

Total: ~220 lines of additional code beyond the base redesign.
