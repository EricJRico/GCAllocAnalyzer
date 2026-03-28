# Future Improvements

## Thread Filter: Persistent Dropdown Panel

**Problem:** The thread filter uses `GenericMenu`, which closes after every click. Selecting or deselecting multiple threads requires repeatedly reopening the dropdown.

**Proposed Solution:** Replace `GenericMenu` with a UIElements dropdown panel that stays open until the user clicks outside. Use a fullscreen transparent overlay to catch the dismiss click. Each thread gets a toggle checkbox that immediately updates the filter without closing the panel.

**Layout:**
```
┌──────────────────────┐
│ All Threads        check │
│ Main Thread Only     │
│ Render Thread Only   │
│ ──────────────────── │
│ [x] Main Thread      │
│ [x] Render Thread    │
│ [x] Worker Thread 0  │
│ [x] Worker Thread 1  │
│ ...                  │
└──────────────────────┘
```

**Scope:** Replace `ShowThreadFilterMenu()`, remove `GenericMenu`/callback methods (`OnThreadMenuAll`, `OnThreadMenuSolo`, `OnThreadMenuToggle`), add persistent panel with toggles and overlay dismiss logic.

## UX: Progress Indicators for Save/Load/Export

**Problem:** Save, Load, and CSV Export operations can take several seconds on large datasets, during which Unity appears frozen with no visual feedback.

**Proposed Solution:** Show a progress indicator (e.g., `EditorUtility.DisplayProgressBar`) during Save, Load, and both CSV export paths. Clear it in a `finally` block to ensure cleanup on errors.

**Scope:** `ExportMarkerTableCSV`, `ExportAllocationsCSV`, Save/Load snapshot handlers.

## Performance: BuildGrouping (~1,000ms for 500K allocations)

`BuildGrouping` iterates all `RawAllocations` twice (once for full-callstack grouping, once for top-frame grouping) with `Dictionary<string, CallsiteGroup>` lookups per allocation. At 500K+ allocations this takes ~1s. Potential optimizations:

- **Integer-keyed grouping** — assign each unique call stack a sequential integer ID during extraction (the call stack cache already identifies them). Use `Dictionary<int, CallsiteGroup>` instead of string dictionary lookups.
- **Pre-sized `Allocations` lists** — groups currently start with `new List<RawAllocation>(16)`, causing many resizes for groups with thousands of allocations. A two-pass approach (count first, then allocate) or a rough size estimate would reduce array copies.
- **Single-pass grouping** — build both groupings simultaneously in one pass over `RawAllocations` instead of two separate passes.

## Performance: Unaccounted Extraction Overhead (~940ms)

Within the frame extraction loop, ~940ms is not attributed to any timed phase. This includes:

- **`GetSampleCallstack`** native interop — called for every GC.Alloc sample (500K+ calls), fills a `List<ulong>` from the native side. This is a Unity API cost that may not be reducible.
- **Address hash computation** — the call stack cache key hash runs for every GC.Alloc sample. Already fast (integer multiply-accumulate), unlikely to improve further.
- **`EditorUtility.DisplayCancelableProgressBar`** — called every 20 frames. Could reduce frequency or remove entirely for small frame ranges.

## Performance: Sample Iteration (~745ms)

The per-sample loop calls `GetSampleMarkerId(i)` and `GetSampleChildrenCount(i)` for every sample on every GC-carrying thread. With many threads and high sample counts, this is ~745ms of Unity API calls. Hard to optimize without a different profiler API (e.g., a bulk/batch sample query if Unity ever provides one).

## Project Auditor Integration: Runtime-Driven Rules

**Goal:** Allow users to push hot allocation sites identified by the analyzer directly into Project Auditor as actionable rules to resolve.

**Workflow:**
1. User profiles their game, GCAllocAnalyzer identifies top offenders (largest per-frame, biggest spikes)
2. User right-clicks an allocation row → *"Add to Project Auditor"*
3. The rule is created with the specific source file and line number

**Line Number Resolution:**
The profiler often provides only the method name without a line number (`SourceLine == 0`), especially with IL2CPP. To create a useful rule, the exact allocation line must be resolved:
- **If `SourceLine` is available** from the profiler (Mono backend with call stacks enabled), use it directly
- **If not**, scan the compiled assembly using **Mono.Cecil** (shipped with Unity) or **Roslyn** to find allocation-site IL instructions (`newobj`, `box`, `newarr`) within the identified method, reading line numbers from PDB debug symbols
- If a single allocation site is found, use it automatically. If multiple, present a disambiguation picker listing each site with its line number and allocation type

**Integration Path:**
- Project Auditor supports both custom modules and Roslyn Analyzer diagnostics
- A **Roslyn Analyzer** approach would surface diagnostics in both Project Auditor's UI and the IDE (VS/Rider squiggles), but rules are static
- A **custom ProjectAuditor Module** could read a serialized report (JSON/ScriptableObject) of hot allocation sites exported by the analyzer, emitting them as Project Auditor issues with descriptors, severity, and source location
- The runtime profiling data provides pre-triaged severity — unlike pure static analysis that flags every `new`, only allocations confirmed as hot at runtime would become rules

**Key Value:** Bridges runtime profiling (what actually allocates, how much, how often) with static analysis (exact source location, trackable resolution workflow). Neither tool provides this alone.

## Bucketed Segment Data: Segments at All Zoom Levels

**Problem:** Stacked colored segments only render at 1:1 zoom (`m_FramesPerBucket == 1`). When zoomed out, bars become solid teal and the overlay falls back to bottom-aligned — so selecting a segment then zooming out causes the overlay to lose its position.

**Root Cause:** Segment data (`m_Segments`/`m_SegmentOffsets`) is indexed per frame, but bucketed bars represent multiple frames. Three gates in `PerFrameGraphController` disable segments when `m_FramesPerBucket > 1`: the `showSegments` flag (line ~403), overlay method resolution (line ~526), and `HitTestSegment` (line ~1791).

**Approaches to Investigate:**
- **Peak frame segments** — for each bucket, find the frame with the highest total bytes and use its existing per-frame segments via an index map (`bucket → peak frame`). No new segment data, just a small `int[]` built during the bar loop. Limitation: only shows one frame's breakdown, methods exclusive to non-peak frames are invisible.
- **Aggregated segments** — sum method bytes across all frames per bucket, scale proportionally to fit the bar's MAX value. More representative but requires rebuilding segment data on every zoom/pan. Use a fixed-size `long[17]` array (palette size) instead of dictionary for allocation-free aggregation.

**Scope:** `PerFrameGraphController.cs` (build bucketed segments, remove three gates), overview element should also receive segments. `GraphElement.cs` needs no changes — it already renders any segments it receives.

## Performance: Binary Temp File for Domain Reload Persistence

**Problem:** `m_Snapshot.RawAllocations` (1.87M+ `RawAllocation` objects) is `[SerializeField]` and Unity serializes it by value during domain reload. Each object has ~10 string fields (thread names, method names, call stack keys) that are massively duplicated across allocations — Unity serializes `"MainThread"` 1.87M times. This produces ~2.5GB of serialized data, causing slow reloads and high memory pressure.

**Current workaround:** `GraphFrameStore`'s cached fields are `[NonSerialized]` (was 4× duplication = 10GB+ → OOM crash). The snapshot itself still serializes ~2.5GB.

**Proposed Solution:** Replace Unity's built-in serialization with a compact binary temp file:
1. In `OnDisable`, write analysis state to a temp file (`FileUtil.GetUniqueTempPathInProject()`) with deduplicated string tables:
   - Build a string intern table (thread names: ~20 unique, call stack keys: ~400 unique, display names: ~400 unique)
   - Write string table once, then per-allocation write only integer indices + non-string fields
   - Expected size: ~50-100MB for 1.87M allocs (vs 2.5GB with Unity serialization)
2. In `TryRestoreAfterReload`, read the binary file back and reconstruct all data
3. Mark `m_Snapshot` as `[NonSerialized]` — only the temp file path needs to survive

**Additional candidates for binary serialization:**
- Any other `[SerializeField]` data containing large collections of objects with repeated string fields
- `FullFrameBytes` (long[]) — already compact, low priority

**Scope:** New `SnapshotSerializer` utility class with `Write(AnalysisSnapshot, string path)` and `Read(string path)` methods. Changes to `OnDisable`/`TryRestoreAfterReload` to use the file instead of Unity serialization.

## Refactor: Simplify GraphElement Rendering Architecture

**Problem:** GraphElement's drawing code juggles multiple overlapping state flags — `m_AnalyzedBarMask`, `m_AnalyzedBarValues`, `m_AnalyzedStartBar/EndBar`, selection range, highlighted bar, overlay bars, stacked segments — and combines them in nested branches to determine each bar's color and height. Every new feature adds another flag and another branch.

### Known Bug: Magnitude-Mode Selection Shifts on Window Resize

**Symptom:** In Order by Size mode, select an area, then resize the window wider. The bright selection area and allocation site overlay shift position instead of staying anchored to the same data.

**Root cause:** Selection dimming in magnitude mode uses position fractions (`m_MagnitudeSelStartFrac/EndFrac` in `PerFrameGraphController.cs` ~line 134-136) to keep the bright area stable across zoom. These fractions represent "bar position X% through Y% of the sorted order." However, when window width changes, `m_FramesPerBucket` changes (computed from `visibleFrameCount / areaWidth` at ~line 472-474), which changes bucket compositions (different frames grouped together), which changes the sorted order itself. Position fraction 50% maps to different data at fpb=4 vs fpb=2.

**The fix — magnitude range instead of position fractions:**

Replace the three position-fraction fields:
```csharp
// PerFrameGraphController.cs ~line 134-136
bool m_HasMagnitudeSelection;
float m_MagnitudeSelStartFrac;   // REMOVE
float m_MagnitudeSelEndFrac;     // REMOVE
```

With magnitude byte-value range:
```csharp
bool m_HasMagnitudeSelection;
long m_MagnitudeSelMinValue;     // ADD — min bar Value in selection
long m_MagnitudeSelMaxValue;     // ADD — max bar Value in selection
```

**Where fractions are stored** — `BuildSelectedFrameBuffer()` at ~line 2456-2462:
```csharp
// Currently:
m_MagnitudeSelStartFrac = (float)(m_ViewportStartBucket + startBar) / totalBars;
m_MagnitudeSelEndFrac = (float)(m_ViewportStartBucket + endBar + 1) / totalBars;
// Replace with:
// Iterate bars[startBar..endBar], track min/max of bars[srcIdx].Value
```

**Where fractions are applied** — `BuildAnalyzedBarMask()` at ~line 2050-2059:
```csharp
// Currently uses fractions to compute brightStart/brightEnd bar indices.
// Replace with: iterate all bars, mark bright if bar.Value >= minValue && bar.Value <= maxValue
```

This is bucket-count-independent because a bar's Value (max GC bytes of its frames) determines selection membership, not its sorted position. A 64KB bar stays in the 50KB-80KB selection range regardless of how frames are bucketed.

**Overlay is also affected:** `DrawOverlayBars()` in `GraphElement.cs` ~line 564-597 gates overlay rendering on `m_AnalyzedBarMask`. Since the mask shifts, the overlay shifts too. Fixing the mask fixes both.

**Partial-brightness values (`m_AnalyzedBarValues`):** The frame-buffer path in `BuildAnalyzedBarMask` (~line 2064-2085) computes per-bar max GC of selected frames via `m_FrameStore.FullFrameBytes`. For the magnitude-range path, values should equal the bar's full value (since the entire bar is selected data in magnitude mode).

### Proposed Refactor: Pre-Computed Render Descriptors

The controller should pre-compute a flat per-bar render descriptor and the renderer should just draw what it's told:

```csharp
struct BarRender
{
    public float Height;   // or long Value, let renderer do Y mapping
    public Color Color;    // final resolved color (normal, dimmed, selected, etc.)
}
```

All bright/dim/selected/highlighted/overlay logic moves to the controller. The renderer becomes a simple loop: for each bar, draw rect at height with color. This eliminates the state flag combinatorics and makes each new feature a change to the controller's pre-computation, not a new branch in the renderer.

**Current state flags in GraphElement that would be replaced:**
- `bool[] m_AnalyzedBarMask` — per-bar bright/dim
- `long[] m_AnalyzedBarValues` — per-bar selected-frame max GC (for partial brightness)
- `int m_AnalyzedStartBar/EndBar` — contiguous range fallback
- `int m_HighlightedBar` — hover highlight
- `int m_SelectionStart/End` — drag selection range
- `BarData[] m_OverlayBars` / `int m_OverlayBarCount` — per-marker overlay
- `BarSegment[] m_Segments` / `int[] m_SegmentOffsets` — stacked colored segments
- `int m_HighlightedMethodIndex` — selected marker tint

**Current rendering branches in `DrawBars()` (~line 420-538) that combine these flags:**
1. Stacked segments vs solid bar (line 478 vs 521)
2. Within solid: selected vs not-analyzed vs analyzed (line 525-530)
3. Within analyzed: partial brightness check via `m_AnalyzedBarValues` (line 533)
4. Highlighted method overlay tint (line 534-543)
5. Hover highlight overlay (line 547)

**Current rendering in `DrawOverlayBars()` (~line 564-597):**
- Separate method that draws per-marker overlay bars gated by the analyzed mask

**Scope:** `GraphElement.cs` drawing methods, `PerFrameGraphController.cs` mask/value computation. The magnitude range fix should be implemented as part of this refactor rather than adding more state to the current architecture.

## BarGraph: Light/Dark Mode and Color Blind Accessibility

**Plan:** [`Documentation~/plans/Plan_LightDarkMode_ColorBlind.md`](plans/Plan_LightDarkMode_ColorBlind.md)

USS-only light/dark mode theming via `.bar-graph--dark` / `.bar-graph--light` class selectors, plus CVD (color vision deficiency) chrome overrides for tritanopia, deuteranopia, and protanopia. Includes `BarGraphPalettes` static class with Okabe-Ito and BlueOrange6 color-blind-safe palettes for consumer data colors. No C# changes to `BarGraphElement.cs`.

## Performance: Minor Allocation Hotspots

Low-priority items flagged during code review. Not urgent — each allocates once per user action or once per analysis, not per frame.

- **`BindAllocNum`** calls `(index+1).ToString()` on every bind — the only allocation in any bind method. Could pre-compute row number strings for visible range.
- **`ClearMarkerSummary`** allocates `new List<RawAllocation>(0)` — aliasing issue prevents simple `.Clear()` since `m_SelectedAllocations` points to a group's allocation list. Acceptable tradeoff.
- **Graph bar creation** creates one `VisualElement` per bucket — consider `generateVisualContent` custom drawing if Compare mode doubles the bucket count and performance suffers.
