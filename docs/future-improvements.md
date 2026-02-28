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

## Performance: Minor Allocation Hotspots

Low-priority items flagged during code review. Not urgent — each allocates once per user action or once per analysis, not per frame.

- **`BindAllocNum`** calls `(index+1).ToString()` on every bind — the only allocation in any bind method. Could pre-compute row number strings for visible range.
- **`ClearMarkerSummary`** allocates `new List<RawAllocation>(0)` — aliasing issue prevents simple `.Clear()` since `m_SelectedAllocations` points to a group's allocation list. Acceptable tradeoff.
- **Graph bar creation** creates one `VisualElement` per bucket — consider `generateVisualContent` custom drawing if Compare mode doubles the bucket count and performance suffers.
