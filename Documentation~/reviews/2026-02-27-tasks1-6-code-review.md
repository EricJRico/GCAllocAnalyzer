# Code Review: Tasks 1-6 Single Panel — 2026-02-27

3 independent reviewers ran in parallel. Findings consolidated below.

---

## Task Completion

| Task | Status |
|------|--------|
| 1 - AnalysisSnapshot | Complete |
| 2 - Save/Load | Complete (1 bug) |
| 3 - Per-frame Stats | Complete (exceeded plan) |
| 4 - Bar Graph | Complete |
| 5 - Exclude/Menus/CSV | Complete |
| 6 - Marker Summary | Complete (intentional deviation — columns instead of right-panel) |

---

## Plan Updates Still Needed

### Task 6 — Rewrite to match actual approach
The plan still describes right-panel enrichment (First Frame, Min/Max clickable links, Top 3 worst frames, per-frame stats display). The actual implementation surfaces these as **sortable table columns** (Median, Mean, Min, Max, Range, First) in the marker list, matching Profile Analyzer's style. This was intentional. Rewrite Task 6 to reflect the column-based approach.

### Task 2 — Already updated
Changed `EditorJsonUtility` references to `JsonUtility` (correct API for plain `[Serializable]` classes).

### Task 4 — Already updated
Changed color coding description to "single bar color matching Profile Analyzer's style".

---

## Bugs to Fix

### 1. `m_IsLoadedSnapshot` not serialized (all 3 reviewers flagged)
**File:** `GCAllocAnalyzerWindow.cs`, line 114

```csharp
// Current:
bool m_IsLoadedSnapshot;

// Fix:
[SerializeField] bool m_IsLoadedSnapshot;
```

Also add to end of `TryRestoreAfterReload()`:
```csharp
if (m_IsLoadedSnapshot)
    m_LoadedSnapshotLabel.style.display = DisplayStyle.Flex;
```

**Impact:** After domain reload on a loaded snapshot, Profiler sync guard resets to false — tool sends stale frame indices to Profiler. Warning banner disappears.

---

## Dead Code to Clean Up

| What | Line(s) | Why |
|------|---------|-----|
| `m_LastNameFilter`, `m_LastExcludeFilter`, `m_LastSelectedThreadCount` | 163-165 | Declared, never read |
| `k_LinkBlue` | 56 | Unused after Task 6 UI revert |
| `long avg = ...` in `UpdateMarkerSummary` | 2466 | Computed, never used |

---

## Stale Section Headers (from MultiColumnListView refactor, NOT Task 6)

### Lines 1214-1216: Empty "SORTABLE COLUMN HEADERS" section
```
// ═══════════════════════════════════════════════════
//  SORTABLE COLUMN HEADERS — userData, no closures
// ═══════════════════════════════════════════════════
//  RIGHT PANEL
// ═══════════════════════════════════════════════════
```
The "SORTABLE COLUMN HEADERS" header has no code — that logic moved into MultiColumnListView. Remove lines 1214-1216, keep "RIGHT PANEL".

### Lines 2615-2617: Duplicate "ALLOC LIST" header
```
// ═══════════════════════════════════════════════════
//  ALLOC LIST — VIRTUALIZED, pre-computed strings
// ═══════════════════════════════════════════════════

// ═══════════════════════════════════════════════════
//  ALLOC LIST — MULTI-COLUMN, no allocs in bind
// ═══════════════════════════════════════════════════
```
First header is stale (pre-MultiColumnListView). Remove lines 2615-2617, keep the second.

---

## Performance Suggestions (not urgent, noted by individual reviewers)

- **Sort lambdas** capture `dir` local, allocating a closure per sort — could use static `Comparison<T>` delegates
- **`BindAllocNum`** calls `(index+1).ToString()` — only allocation in any bind method
- **`BuildThreadIndex`** allocates `new HashSet<string>` per group — could pool
- **`ComputeSnapshotPerFrameBytes`** always allocates new `long[]` — could reuse like `EnsurePerFrameBuffer`
- **`ClearMarkerSummary`** allocates `new List<RawAllocation>(0)` — aliasing issue prevents simple `.Clear()`
- **Graph bar creation** creates one VisualElement per bucket — consider `generateVisualContent` if scaling becomes an issue for Compare mode
- **`m_PerFrameBuffer`** shared between `ComputePerFrameStats` and `UpdateGraphOverlay` — safe today (single-threaded, never concurrent) but fragile; add documentation comment

---

## Architecture Suggestion for Tasks 7-12

Reviewer C recommended extracting before Compare mode adds ~1,400 more lines:
1. Data model classes -> `GCAllocAnalyzerData.cs` (~110 lines, 10 min)
2. CSV export -> `GCAllocExporter.cs` (~110 lines, 10 min)
3. Script-opening logic -> `ScriptOpener.cs` (~200 lines, 15 min)
4. Graph controller -> `PerFrameGraphController.cs` (~250 lines, 20 min)

This would reduce main file from ~3,300 to ~2,600 lines before Compare mode growth.
