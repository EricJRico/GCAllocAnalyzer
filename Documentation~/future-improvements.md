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

## Performance: Minor Allocation Hotspots

Low-priority items flagged during code review. Not urgent — each allocates once per user action or once per analysis, not per frame.

- **`BindAllocNum`** calls `(index+1).ToString()` on every bind — the only allocation in any bind method. Could pre-compute row number strings for visible range.
- **`ClearMarkerSummary`** allocates `new List<RawAllocation>(0)` — aliasing issue prevents simple `.Clear()` since `m_SelectedAllocations` points to a group's allocation list. Acceptable tradeoff.
- **Graph bar creation** creates one `VisualElement` per bucket — consider `generateVisualContent` custom drawing if Compare mode doubles the bucket count and performance suffers.
