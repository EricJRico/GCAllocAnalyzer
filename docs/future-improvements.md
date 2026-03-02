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

## Performance: Minor Allocation Hotspots

Low-priority items flagged during code review. Not urgent — each allocates once per user action or once per analysis, not per frame.

- **`BindAllocNum`** calls `(index+1).ToString()` on every bind — the only allocation in any bind method. Could pre-compute row number strings for visible range.
- **`ClearMarkerSummary`** allocates `new List<RawAllocation>(0)` — aliasing issue prevents simple `.Clear()` since `m_SelectedAllocations` points to a group's allocation list. Acceptable tradeoff.
- **Graph bar creation** creates one `VisualElement` per bucket — consider `generateVisualContent` custom drawing if Compare mode doubles the bucket count and performance suffers.
