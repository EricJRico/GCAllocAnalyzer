# Revised Compare Mode Implementation Plan (Tasks 8–13)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a Compare mode that lets users load/pull two independent GC allocation snapshots and view side-by-side deltas — paired per-frame graphs, a delta marker table, and regression/improvement summaries.

**Architecture:** A mode tab bar switches between Single (existing) and Compare. Compare mode is encapsulated in a new `CompareController` class (mirroring the `PerFrameGraphController` pattern) that owns all compare UI, data, and logic. Single mode is never modified — Compare builds entirely alongside it.

**Tech Stack:** C# 9.0, Unity 6000.0 UIElements (`MultiColumnListView`, `TwoPaneSplitView`, `Painter2D`), `JsonUtility` serialization.

**Branch:** Create feature branch `feature/compare-mode` from `develop` before starting. All commits go to this branch.

---

## Quality Gate Process (Mandatory Per Task)

Every task follows this cycle. Do NOT skip steps or batch tasks.

```
Implement → Build → Code Review → Fix Issues → Re-Build → Re-Review → Approve → MEMORY.md → Commit
```

### Step-by-step:

1. **Implement** the task per its spec
2. **Build check** — `dotnet build` the `.csproj` in the dev project. Compilation errors = stop and fix. (Unity silently runs OLD code on compile failure — never skip this.)
3. **Code review** — Use `superpowers:requesting-code-review` agent. Review against:
   - The **Universal Review Checklist** below
   - The **task-specific review criteria** in each task section
4. **Fix issues** found by the reviewer. Every issue must be resolved.
5. **Re-build** after fixes
6. **Re-review** — Run the code review agent again to confirm fixes are clean
7. **Approve** — Only proceed when the reviewer finds no issues
8. **Update MEMORY.md** — Record: what was completed, architectural decisions made, any deviations from plan
9. **Commit** with the specified commit message

### Universal Review Checklist

Applied to EVERY task. The code reviewer must verify ALL of these:

**Performance & Allocations:**
- [ ] No LINQ in hot paths (explicit `for` loops)
- [ ] Reusable buffers used (`StringBuilder`, `List<T>`, `Dictionary`, arrays) — no per-frame allocations
- [ ] Pre-computed display strings (built once during data processing, not during UI bind)
- [ ] No closures in UI bind methods (use `userData`-based callbacks)
- [ ] Object pooling where applicable (display rows, etc.)

**Naming & Style:**
- [ ] `m_` prefix for private instance fields
- [ ] `k_` prefix for constants
- [ ] `// ═══` section separators between major code sections
- [ ] No unnecessary comments or docstrings on code that wasn't changed

**Architecture & SOLID:**
- [ ] Single mode code is UNCHANGED (no modifications to existing Single mode methods)
- [ ] Dependency flows one way: `CompareController → Window`, never reverse
- [ ] No Compare-mode types referenced by Single mode code
- [ ] If Compare mode files were deleted, Single mode would compile and run unchanged
- [ ] New code follows existing patterns (match neighboring code style)

**Unity UIElements Patterns:**
- [ ] Context menus use `AddManipulator(new ContextualMenuManipulator(...))`, NOT `RegisterCallback<ContextualMenuPopulateEvent>`
- [ ] `MultiColumnListView` uses `FixedHeight` virtualization, custom sort handling
- [ ] `userData`-based callbacks in bind methods (no closures)
- [ ] Unbind methods reset styles to `StyleKeyword.Null`
- [ ] `GraphElement.focusable` with auto-focus on `PointerEnter` for keyboard input

**Clean Code (Non-Negotiable from CLAUDE.md):**
- [ ] No defensive null checks on guaranteed-initialized fields
- [ ] No unnecessary code — every line exists for a reason
- [ ] Null checks ONLY when null is a legitimate expected state (e.g., `ComparedGroup.Left` being null for right-only groups)

**Serialization:**
- [ ] `[SerializeField]` on fields that must survive domain reload
- [ ] `CompareControllerState` struct pattern (matching `GraphControllerState`)
- [ ] `TryRestoreAfterReload()` rebuilds all non-serialized state

---

## Context

The existing tool only analyzes a single profiling session. Compare mode enables A/B profiling workflows: capture a baseline, make an optimization, capture again, and see exactly which allocation sites improved or regressed. This mirrors Unity's Profile Analyzer compare functionality but focused exclusively on GC allocations.

Tasks 1–7 (Single mode foundation) are complete. This plan covers Tasks 8–13 from the original `Documentation~/plans/task-plan-gcalloc-analyzer.md`, revised against the actual codebase as of commit `7f8ad6e` and a detailed review against the actual Profile Analyzer UI.

### Profile Analyzer Reference (from screenshot)
- **Mode tabs**: "Single" and "Compare" tabs at top-left
- **Toolbar**: 2 compact rows — each: `Pull Data | Load | Save | filename | mini-strip | frame info`
- **Graphs**: Stacked vertically, very compact (~60-80px each)
- **"Pair Graph Selection" checkbox**: Between the two graphs, with a lock icon
- **Filters section**: Name Filter, Exclude Names, Thread, Analysis Type, Marker Columns, Ratio dropdowns. [Compare] button.
- **Marker list columns**: Marker Name, Left Mean, <, >, Right Mean, Diff, Abs Diff, Count L, Count R, Count Diff
- **Right panel**: Frame Summary (L/R/Diff grid), Thread Summary, Marker Summary with "Top 3 by frame costs" horizontal bars

---

## Critical Files

| File | Role |
|------|------|
| `Editor/AnalysisEngine.cs` | **NEW** — static class with extracted pure analysis logic (Task 7.5) |
| `Editor/GCAllocAnalyzerData.cs` | Data structures — add `ComparedGroup`, `CompareControllerState`, `FormatSignedBytes` |
| `Editor/GCAllocAnalyzerWindow.cs` (~3630 lines) | Main window — mode tabs, thin wrappers to AnalysisEngine, wire CompareController |
| `Editor/CompareController.cs` | **NEW** — all compare UI, data, and logic |
| `Editor/PerFrameGraphController.cs` (~2270 lines) | Graph controller — compact mode, shared Y-axis, SetSelection API |
| `Editor/GraphElement.cs` (~819 lines) | Graph renderer — no changes expected |
| `Editor/GCAllocExporter.cs` | CSV export — add compare export method |

---

## Architectural Decisions (from codebase review)

These decisions were made after reviewing the actual codebase against the original plan. They are final.

### AnalysisEngine extraction (Task 7.5)
Pure analysis computation moves to a static `AnalysisEngine` class. Window methods become thin wrappers. CompareController calls `AnalysisEngine` directly with its own data. This avoids modifying Single mode's private methods while making logic reusable. If Compare mode is deleted, `AnalysisEngine` remains as useful infrastructure.

### CompareController ↔ Window wiring
Direct window reference: `CompareController(GCAllocAnalyzerWindow window)`. No interface — CompareController is inherently a sub-panel of this window and will never be instantiated elsewhere. Window exposes `internal ExtractProfilerData()`, `internal SaveSnapshotToFile()`, `internal LoadSnapshotFromFile()`. These share transient caches (cleared each call).

### PerFrameGraphController changes (additive only)
- `k_GraphHeight` → `readonly float m_GraphHeight` via constructor param `bool compactMode = false`. Default preserves Single mode behavior.
- New `public long AutoYAxisMax` property (read-only). New `SetSharedYAxisMax(long max)` method.
- New `public void SetSelection(int startFrame, int endFrame, bool suppressEvents = false)` with guard flag to prevent infinite Left↔Right recursion.
- Single mode never calls any of these additions.

### Thread filter in compare mode
Filters at the `ComparedGroup` level (after comparison), matching how Single mode filters already-grouped data. CompareController maintains its own independent thread state (`m_CompareAllThreadNames`, `m_CompareSelectedThreads`, `m_CompareGroupThreadIndex`).

### Pair Graph Selection sync
Uses normalized percentage mapping based on full frame range (not viewport-relative). `normalizedStart = (startFrame - fullStart) / (float)(fullEnd - fullStart)`, mapped to the other graph's frame range. Requires the new `SetSelection()` API.

### Frame selection from loaded snapshots
CompareController passes a guarded callback for `onFrameSelected` that skips Profiler navigation when data is from a loaded snapshot (frame indices may not exist in current Profiler session).

### Impact on Single Mode

| Change | Type | Risk |
|--------|------|------|
| Analysis methods delegate to `AnalysisEngine` | Mechanical refactor | Zero — same logic |
| `PerFrameGraphController` gets optional `compactMode` param | Additive | Zero — default=false |
| `PerFrameGraphController` gets new public methods/properties | Additive | Zero — never called by Single mode |
| Window gets `internal ExtractProfilerData/Save/Load` | Additive | Zero — never called by Single mode |
| `k_GraphHeight` becomes `readonly m_GraphHeight` | Internal | Zero — same default value |

---

## Task 7.5: Extract AnalysisEngine (Refactoring Only)

**Goal:** Move pure analysis computation to `AnalysisEngine` static class. Zero behavior changes to Single mode. This makes analysis logic reusable by CompareController without modifying existing code paths.

**Files:**
- Create: `Editor/AnalysisEngine.cs`
- Modify: `Editor/GCAllocAnalyzerWindow.cs` (mechanical delegation only — wrappers call `AnalysisEngine`)

### What Changes

1. **Create `Editor/AnalysisEngine.cs`** — static class in `GCAllocBreakdown.Editor` namespace with:

   | Static Method | Extracted From | Key Parameters |
   |--------------|---------------|----------------|
   | `BuildGrouping` | Window:2398 | `(bool byCallsite, List<CallsiteGroup> target, List<RawAllocation> allocs, long totalBytes, bool showAssembly, Dictionary<string,CallsiteGroup> workDict)` |
   | `ComputeGroupStats` | Window:2442 | `(List<CallsiteGroup> target, long total, int frameStart, int frameEnd, long[] perFrameBuffer, StringBuilder sharedSB)` |
   | `ComputePerFrameStats` | Window:2531 | `(CallsiteGroup group, int frameStart, int frameEnd, long[] perFrameBuffer)` |
   | `ComputeSnapshotPerFrameBytes` | Window:2633 | `(AnalysisSnapshot snapshot)` |
   | `InitGroupSlots` | Window (private) | `(List<CallsiteGroup> cached, List<CallsiteGroup> target)` |
   | `RemoveEmptyGroups` | Window:2388 | `(List<CallsiteGroup> groups)` |

2. **Window methods become thin wrappers** — each existing private method delegates to `AnalysisEngine` with its instance fields:
   ```csharp
   void BuildGrouping(bool byCallsite, List<CallsiteGroup> target)
   {
       AnalysisEngine.BuildGrouping(byCallsite, target, m_Snapshot.RawAllocations,
           m_Snapshot.TotalBytes, m_ShowAssembly, m_GroupingDict);
   }
   ```

3. **Extract Save/Load as static helpers** on `AnalysisEngine` or a separate utility:
   ```csharp
   internal static AnalysisSnapshot LoadSnapshotFromJson(string json)
   internal static string SaveSnapshotToJson(AnalysisSnapshot snapshot)
   ```
   Window's `OnSaveSnapshot()`/`OnLoadSnapshot()` handle the file dialog + UI updates, calling these for the actual serialization.

4. **Add `internal ExtractProfilerData()`** on window — thin wrapper over the data-extraction portion of `RunAnalysis()`. Shares window's transient caches (cleared each call). CompareController will call this.

### What Doesn't Change
- `RunAnalysis()` logic, flow, and behavior (just delegates to `AnalysisEngine` for computation steps)
- All UI code, callbacks, filter logic
- `RebuildFromCache()`/`RebuildFromCacheWithBuffer()` stay on window for now (extracted data-core in a later task if needed)
- No visible changes to Single mode

### Definition of Done
- `AnalysisEngine.cs` exists with all static methods
- Window methods delegate to `AnalysisEngine` — logic is identical
- Single mode works exactly as before (build + manual test)

### Task-Specific Review Criteria
- [ ] Every `AnalysisEngine` static method is a pure function (no static state, no UI references)
- [ ] Window wrappers pass only the necessary instance fields — no `this` reference passed
- [ ] `AnalysisEngine` does NOT reference `UnityEditor` UI types (no `Label`, `VisualElement`, etc.)
- [ ] Method bodies in `AnalysisEngine` are exact copies — no logic changes, no "improvements"
- [ ] Window's `RunAnalysis()` flow reads identically to before (same phases, same order)
- [ ] Timing/stopwatch code stays in window wrappers (not extracted to `AnalysisEngine`)
- [ ] `AnalysisEngine` assembly reference is Editor-only (same asmdef)
- [ ] Git diff shows window methods are shorter (delegation) but call sites unchanged

### Commit
```
refactor: extract AnalysisEngine for shared analysis logic (Task 7.5)
```

---

## Task 8: Mode Tabs + CompareController Skeleton

**Goal:** Tab bar at top of window to switch between Single and Compare modes. Create `CompareController` class with empty shell.

**Files:**
- Modify: `Editor/GCAllocAnalyzerWindow.cs`
- Create: `Editor/CompareController.cs`

### What Changes

1. **New enum** in the window class:
   ```csharp
   enum AnalysisMode { Single, Compare }
   [SerializeField] AnalysisMode m_CurrentMode = AnalysisMode.Single;
   ```

2. **Tab bar** — built in `CreateGUI()` ABOVE the toolbar. Two-button row styled like Profile Analyzer tabs:
   ```csharp
   VisualElement BuildModeTabBar()
   ```

3. **Mode containers** — wrap existing Single UI and new Compare UI:
   ```
   rootVisualElement
   ├── BuildModeTabBar()
   ├── m_SingleModeRoot         ← wraps existing toolbar + splitView
   ├── m_CompareModeRoot        ← CompareController.Root
   ├── BuildStatusBar()         ← shared across modes
   └── m_GraphController.TooltipElement
   ```

4. **`OnModeChanged(AnalysisMode mode)`:**
   - Toggle `DisplayStyle` on `m_SingleModeRoot` / `m_CompareModeRoot`
   - Update tab button styling

5. **CompareController skeleton** (`Editor/CompareController.cs`):
   ```csharp
   internal class CompareController
   {
       public VisualElement Root { get; }
       readonly Label m_StatusLabel;
       readonly StringBuilder m_SharedSB;

       public CompareController(Label statusLabel, StringBuilder sharedSB)
       {
           Root = BuildUI();
       }

       VisualElement BuildUI()
       {
           // Placeholder label "Compare mode — loading..."
       }

       public void OnDisable() { }
       public void TryRestoreAfterReload() { }
   }
   ```

6. **Domain reload:** `m_CurrentMode` is `[SerializeField]` — `TryRestoreAfterReload()` restores mode visibility and calls `m_CompareController.TryRestoreAfterReload()`.

7. **Refactor Save/Load helpers** — extract from window for reuse by CompareController:
   ```csharp
   // In window — parameterized helpers callable by both Single and Compare mode
   internal AnalysisSnapshot LoadSnapshotFromFile()  // returns loaded snapshot or null, no UI side effects
   internal void SaveSnapshotToFile(AnalysisSnapshot snapshot, Label statusLabel)
   ```
   Single mode's `OnSaveSnapshot()` / `OnLoadSnapshot()` become thin wrappers. CompareController calls the same helpers.

### What Doesn't Change
- All existing Single mode code, layout, and behavior
- `BuildToolbar()`, `BuildLeftPanel()`, `BuildRightPanel()` remain untouched

### Definition of Done
- Two tabs visible at top of window
- Clicking "Single" shows current tool identically to before
- Clicking "Compare" shows placeholder from CompareController.Root
- Mode survives domain reload
- `CompareController.cs` exists with skeleton + constructor

### Task-Specific Review Criteria
- [ ] `BuildToolbar()`, `BuildLeftPanel()`, `BuildRightPanel()` have ZERO changes (diff check)
- [ ] `m_SingleModeRoot` wraps ALL existing UI — no element reparenting that could break layout
- [ ] Tab styling matches Profile Analyzer (active = highlighted, inactive = dim)
- [ ] `OnModeChanged` only toggles `DisplayStyle`, no data recomputation
- [ ] `CompareController` constructor takes `GCAllocAnalyzerWindow` (direct reference, per review plan)
- [ ] Save/Load helpers are truly side-effect-free — no UI mutations, return data only
- [ ] `m_CurrentMode` is `[SerializeField]` and restores correctly after domain reload
- [ ] `AnalysisEngine.cs` created with extracted static methods (per review plan Task 7.5)
- [ ] Window's `BuildGrouping`/`ComputeGroupStats`/etc. are thin wrappers delegating to `AnalysisEngine`

### Commit
```
feat: add Single/Compare mode tab bar and CompareController skeleton (Task 8)
```

---

## Task 9: Compare Data Model

**Goal:** Data structures for holding two snapshots and computing deltas between matched groups.

**Files:**
- Modify: `Editor/GCAllocAnalyzerData.cs` — add `ComparedGroup`, `FormatSignedBytes`, enums
- Modify: `Editor/CompareController.cs` — add fields and `BuildComparison()` method

### What Changes

1. **New enums in `GCAllocAnalyzerData.cs`**:
   ```csharp
   internal enum CompareStatMode { Mean, Median, TotalBytes, Min, Max }
   internal enum CompareRatioMode { Raw, Normalized }
   ```

2. **New utility in `GCAllocUtils`**:
   ```csharp
   public static string FormatSignedBytes(long bytes)
   {
       // "+1.2 KB", "-256 B", "0 B"
       if (bytes == 0) return "0 B";
       string prefix = bytes > 0 ? "+" : "";  // negative sign comes from the number
       return string.Concat(prefix, FormatBytes(Math.Abs(bytes)));
   }
   ```

3. **New class in `GCAllocAnalyzerData.cs`** (after `CallsiteGroup`):
   ```csharp
   [Serializable]
   internal class ComparedGroup
   {
       public CallsiteGroup Left;       // null if right-only
       public CallsiteGroup Right;      // null if left-only
       public string DisplayName;
       public string Key;

       // Raw deltas (positive = regression, negative = improvement)
       public long DeltaBytes;
       public int DeltaCount;
       public double DeltaMean;
       public long DeltaMedian;
       public long DeltaMin;
       public long DeltaMax;
       public float DeltaPercent;       // percentage change relative to Left baseline

       // Pre-computed display strings for the ACTIVE CompareStatMode only.
       // Recomputed when stat mode or ratio mode changes.
       public string FormattedLeft;
       public string FormattedRight;
       public string FormattedDiff;
       public string FormattedLeftCount;
       public string FormattedRightCount;
       public string FormattedDiffCount;
       public string FormattedDeltaPercent;  // "+12.3%" or "new" or "removed"

       public bool IsRegression => DeltaBytes > 0;
       public bool IsImprovement => DeltaBytes < 0;
       public bool IsLeftOnly => Right == null;
       public bool IsRightOnly => Left == null;

       public List<ResolvedFrame> ResolvedCallStack =>
           Left?.ResolvedCallStack ?? Right?.ResolvedCallStack;
   }
   ```
   Key design decisions vs original plan:
   - **Named fields, not `string[]` arrays** — matches `CallsiteGroup` convention
   - **Single set of formatted strings for active stat mode** — recomputed on mode change, not all 5 variants stored
   - **`DeltaPercent` included** — was missing in original
   - **`ResolvedCallStack` accessor** — convenience for Task 13

4. **CompareController data fields**:
   ```csharp
   // Compare mode data (serialized via window [SerializeField] forwarding)
   [SerializeField] AnalysisSnapshot m_LeftSnapshot = new();
   [SerializeField] AnalysisSnapshot m_RightSnapshot = new();
   [SerializeField] GraphFrameStore m_LeftFrameStore = new();
   [SerializeField] GraphFrameStore m_RightFrameStore = new();
   [SerializeField] bool m_IsLeftLoaded;
   [SerializeField] bool m_IsRightLoaded;
   [SerializeField] CompareStatMode m_CompareStatMode = CompareStatMode.Mean;
   [SerializeField] CompareRatioMode m_CompareRatioMode = CompareRatioMode.Normalized;
   [SerializeField] bool m_CompareGroupByCallsite = true;

   readonly List<ComparedGroup> m_ComparedGroups = new(256);
   readonly List<ComparedGroup> m_FilteredComparedGroups = new(256);
   ```
   Note: `m_ComparedGroups` is non-serialized (`readonly` without `[SerializeField]`). After domain reload, `TryRestoreAfterReload()` re-runs `BuildComparison()` from frame store caches.

5. **`BuildComparison()` method** on CompareController:
   - Sources groups from `m_LeftFrameStore.CachedGroupsByFullCallstack/TopFrame` (NOT from snapshot's `[NonSerialized]` lists)
   - Single shared `m_CompareGroupByCallsite` toggle selects which group list for BOTH sides
   - Builds `Dictionary<string, CallsiteGroup>` from left groups (keyed by `Key`)
   - Iterates right groups, matches by `Key`
   - Creates `ComparedGroup` for each: matched, left-only, right-only
   - When `CompareRatioMode.Normalized`: normalizes by frame count before computing deltas
   - `DeltaPercent`: `Left != null && Left.TotalBytes > 0 → (DeltaBytes / (float)Left.TotalBytes) * 100f`; right-only → display "new"; left-only → display "removed"
   - Pre-formats display strings for the active `m_CompareStatMode`
   - Auto-triggered when both sides have data (after Analyze/Load completes on either side)
   - Also callable manually via [Compare] button (for re-trigger after filter/grouping changes)

6. **`RecomputeDisplayStrings()`** — called when `m_CompareStatMode` or `m_CompareRatioMode` changes:
   - Iterates `m_ComparedGroups`, recomputes `FormattedLeft/Right/Diff` for the new active stat
   - Refreshes marker list binding

### What Doesn't Change
- Single mode data (`m_Snapshot`, `m_FrameStore`) untouched
- No UI changes in this task

### Serialization Strategy
`CompareController` is not an `EditorWindow` so it can't use `[SerializeField]` directly. Options:
- **A)** Window holds `[SerializeField] CompareControllerState` struct that the controller reads/writes in `OnDisable()`/`TryRestoreAfterReload()` (mirrors `GraphControllerState` pattern)
- **B)** Window holds the `[SerializeField]` snapshot/framestore fields and passes them to the controller

**Use pattern A** — matches the existing `GraphControllerState` precedent. Create a `CompareControllerState` struct in `GCAllocAnalyzerData.cs`.

### Definition of Done
- `ComparedGroup` class compiles and serializes
- `BuildComparison()` produces correct matched/unmatched groups
- `RecomputeDisplayStrings()` updates formatted strings when stat mode changes
- `CompareControllerState` serialization struct exists

### Task-Specific Review Criteria
- [ ] `ComparedGroup` field naming matches `CallsiteGroup` convention (no arrays, named fields)
- [ ] `BuildComparison()` uses reusable `Dictionary<string, CallsiteGroup>` (not `new` each call)
- [ ] `FormatSignedBytes` uses `string.Concat` (no interpolation, no `StringBuilder` for small strings)
- [ ] `DeltaPercent` handles edge cases: left-only ("removed"), right-only ("new"), zero left bytes
- [ ] `CompareControllerState` struct mirrors `GraphControllerState` pattern exactly
- [ ] No `[SerializeField]` directly on CompareController fields (must go through state struct on window)
- [ ] `m_ComparedGroups` is `readonly` non-serialized list (rebuilt from cache on domain reload)
- [ ] `RecomputeDisplayStrings()` only iterates + reformats — no sorting, filtering, or data recomputation
- [ ] `GCAllocAnalyzerData.cs` changes don't modify any existing types (additive only)

### Commit
```
feat: add ComparedGroup data model and BuildComparison() (Task 9)
```

---

## Task 10: Compare Toolbar (Compact Dual Pull/Load Rows)

**Goal:** In Compare mode, replace the placeholder with two compact toolbar rows — matching Profile Analyzer's layout: `Pull Data | Load | Save | filename/status | mini-strip | frame info`.

**Files:**
- Modify: `Editor/CompareController.cs`
- Modify: `Editor/GCAllocAnalyzerWindow.cs` (extract `RunAnalysisCore`)

### What Changes

1. **Compact toolbar rows** — each row has:
   ```
   ┌──────────────────────────────────────────────────────────────┐
   │ [Left ●]  [Pull Data] [Save] [Load] │ filename.json │ 262fr │
   ├──────────────────────────────────────────────────────────────┤
   │ [Right ●] [Pull Data] [Save] [Load] │ filename.json │ 262fr │
   └──────────────────────────────────────────────────────────────┘
   ```
   Key differences from original plan:
   - **No separate [Analyze] button** — Pull Data includes analyze (same as Single mode's Pull→Analyze flow)
   - **No frame range integer fields per row** — graph drag-select handles sub-range selection
   - **No [Export] button per row** — moved to right-click context menu or Compare-level export
   - **Compact filename + frame count status** instead of full inline status labels
   - **[Swap ↔] button** between the two rows to swap Left/Right data

2. **Extract `RunAnalysisCore` from window** — the data extraction portion of `RunAnalysis()` (lines 1452-1836), parameterized:
   ```csharp
   // In window — internal so CompareController can call it
   internal void RunAnalysisCore(
       AnalysisSnapshot snapshot, GraphFrameStore frameStore,
       int startFrame, int endFrame,
       Action<string> onStatus, Action<float> onProgress)
   ```
   This covers: cache clearing, profiler extraction, grouping, per-frame stats, frame store caching. Returns via populated `snapshot` and `frameStore`. The caller handles all UI updates.

   Single mode's `RunAnalysis()` becomes: `RunAnalysisCore(m_Snapshot, m_FrameStore, ...) + UI updates`.

3. **Compare callbacks**:
   - `OnLeftPullData()` / `OnRightPullData()` — calls `RunAnalysisCore` with left/right snapshot/store, then updates left/right status, then auto-triggers `BuildComparison()` if both sides loaded
   - `OnLeftSave()` / `OnRightSave()` — calls `SaveSnapshotToFile` helper
   - `OnLeftLoad()` / `OnRightLoad()` — calls `LoadSnapshotFromFile` helper, then rebuilds grouping + frame store, then auto-triggers `BuildComparison()`

4. **`m_CompareModeRoot` layout**:
   ```
   m_CompareModeRoot (CompareController.Root)
   ├── BuildCompareToolbar()
   ├── TwoPaneSplitView (Horizontal)
   │   ├── CompareLeftPanel  ← Task 11
   │   └── CompareRightPanel ← Task 13
   └── (tooltips added to window root)
   ```

5. **Frame range for Pull Data**: Uses the profiler's current frame range (same logic as Single mode's `OnPullData()`). Compare mode doesn't need its own frame range fields — the user either pulls current profiler data or loads a saved snapshot.

### What Doesn't Change
- Single mode toolbar layout and visible behavior identical
- `BuildToolbar()` unchanged (Single mode's `RunAnalysis()` becomes thin wrapper)
- No changes to analysis logic, profiler extraction, or grouping algorithms

### Definition of Done
- Compare mode shows two compact toolbar rows
- Each row can independently Pull Data, Save, Load
- [Swap ↔] button swaps left/right data
- Auto-triggers `BuildComparison()` when both sides have data
- Status bar shows which sides are populated

### Task-Specific Review Criteria
- [ ] `ExtractProfilerData` is `internal` on window, reuses window's transient caches (cleared each call)
- [ ] Single mode's `RunAnalysis()` is unchanged — only new `ExtractProfilerData` added alongside
- [ ] Pull Data callbacks use `AnalysisEngine.BuildGrouping()` (not window's private wrapper)
- [ ] Load callbacks rebuild groupings via `AnalysisEngine` then populate frame store cache
- [ ] Swap button swaps ALL state (snapshots, frame stores, loaded flags, status labels) — not just references
- [ ] Button enabled/disabled state is correct (Save disabled when no data, etc.)
- [ ] Toolbar row styling is compact — verify against Profile Analyzer reference
- [ ] No frame range IntegerFields in compare toolbar (graph drag-select handles sub-ranges)

### Commit
```
feat: add compact dual-row compare toolbar with independent L/R data (Task 10)
```

---

## Task 11: Compare Left Panel (Paired Marker List with Delta Columns)

**Goal:** The marker list in Compare mode shows `ComparedGroup` items with left/right values, delta columns, and color-coded regressions/improvements.

**Files:**
- Modify: `Editor/CompareController.cs`

### What Changes

1. **New compare marker list** — `MultiColumnListView m_CompareMarkerListView`:
   ```
   Columns: Allocation Site | Left [stat] | Right [stat] | Diff | Abs Diff | Count L | Count R | Count Diff
   ```
   - `itemsSource = m_FilteredComparedGroups`
   - Same virtualization settings as single-mode marker list
   - **Abs Diff and Count L/R/Diff hidden by default** — visible via column header right-click. Default visible: Site, Left, Right, Diff, Count Diff (5 columns)
   - Column headers dynamically update based on `m_CompareStatMode` (e.g., "Left Mean", "Right Mean")

2. **Configurable primary stat** — dropdown:
   ```csharp
   // "Marker Columns:" dropdown with Mean, Median, TotalBytes, Min, Max
   ```
   Changing stat → calls `RecomputeDisplayStrings()` → refreshes list

3. **Ratio mode** — dropdown:
   - **Raw:** deltas are `Right.Stat - Left.Stat`
   - **Normalized:** normalizes by frame count before delta
   - Default: Normalized
   - Changing ratio → re-runs `BuildComparison()` (recomputes all deltas)

4. **Bind methods** — follow existing pattern (no closures, userData-based):
   - Diff columns colored: `k_Improvement` (green) for negative, `k_Regression` (red) for positive
   - Left-only groups show "—" in Right columns, Right-only show "—" in Left
   ```csharp
   static readonly Color k_Regression = new(0.9f, 0.4f, 0.4f);
   static readonly Color k_Improvement = new(0.4f, 0.85f, 0.5f);
   ```

5. **Independent compare filter controls**:
   ```csharp
   TextField m_CompareNameFilter;
   TextField m_CompareExcludeFilter;
   Toggle m_CompareGroupByCallsite;  // single toggle for BOTH sides
   Button m_CompareThreadFilterBtn;
   Button m_CompareBtn;              // [Compare] button for manual re-trigger
   ```
   - Thread filter shows union of `m_LeftSnapshot.SortedThreadNames` + `m_RightSnapshot.SortedThreadNames`
   - `ApplyCompareFilters()` filters `m_ComparedGroups` → `m_FilteredComparedGroups`
   - **[Compare] button** for manual re-trigger after filter/grouping changes

6. **Compare sort** — default: Diff descending by absolute value (biggest changes first):
   ```csharp
   enum CompareSortCol { LeftStat, RightStat, Diff, AbsDiff, LeftCount, RightCount, DiffCount, Name }
   ```

7. **Selection** — `OnCompareMarkerSelectionChanged()`:
   - Updates right panel detail (Task 13)
   - Updates graph overlays on both paired graphs (Task 12)

8. **Compare left panel layout**:
   ```
   CompareLeftPanel
   ├── Filters foldout
   │   └── Name, Exclude, Thread, GroupBy, Marker Columns, Ratio, [Compare]
   ├── TwoPaneSplitView (Vertical)
   │   ├── Compare graphs (Task 12)
   │   └── m_CompareMarkerListView
   ```

### What Doesn't Change
- Single mode marker list (`m_MarkerListView`) untouched

### Definition of Done
- Compare mode shows a marker list with L/R/Delta columns
- Regressions are red, improvements are green
- Sorting works on all columns (default: |Diff| descending)
- Independent Name/Exclude filters work
- Left-only and right-only groups display correctly with "—" placeholders
- Stat mode and ratio mode dropdowns work

### Task-Specific Review Criteria
- [ ] `MultiColumnListView` setup matches Single mode pattern: `FixedHeight` virtualization, `MARKER_ROW_HEIGHT`, custom sort
- [ ] Bind methods use `userData`-based pattern — verify NO closures or lambdas capturing state
- [ ] Unbind methods reset `style.color` and `style.unityFontStyleAndWeight` to `StyleKeyword.Null`
- [ ] Context menus use `AddManipulator(new ContextualMenuManipulator(...))` pattern
- [ ] Column sort handler follows `OnMarkerColumnSortingChanged` pattern (switch on column name)
- [ ] `ApplyCompareFilters()` is a pure filter (no side effects beyond populating `m_FilteredComparedGroups`)
- [ ] Filter controls are independent from Single mode (own `m_CompareNameFilter`, etc.)
- [ ] Thread filter uses union of both snapshots' thread names
- [ ] "—" placeholders for left-only/right-only groups don't trigger color styling
- [ ] `k_Regression`/`k_Improvement` colors are `static readonly`, not allocated per-bind

### Commit
```
feat: add compare marker list with delta columns (Task 11)
```

---

## Task 12: Compare Per-Frame Graphs (Paired Left/Right)

**Goal:** Two `PerFrameGraphController` instances — compact, stacked vertically (matching Profile Analyzer), with full Single mode capabilities plus synced selection and sub-range re-comparison.

**Files:**
- Modify: `Editor/CompareController.cs`
- Modify: `Editor/PerFrameGraphController.cs` (minor — add label, shared Y-axis support)

### What Changes

1. **Two graph controller instances** in CompareController:
   ```csharp
   PerFrameGraphController m_LeftGraphController;
   PerFrameGraphController m_RightGraphController;
   ```

2. **Compact graph height** — compare mode graphs use a reduced height:
   - Add `SetCompactMode()` or constructor parameter to `PerFrameGraphController` that reduces `k_GraphHeight` from 120f to ~70f
   - Overview strips remain but are proportionally smaller
   - All capabilities retained: zoom, pan, WASD, drag-select, segments, overlay

3. **Layout** — stacked vertically with labels:
   ```
   CompareGraphSection
   ├── "Left (Baseline)" label          ← blue accent
   ├── m_LeftGraphController.Root       ← compact graph
   ├── graph options row: [✓ Pair Graph Selection 🔒] [✓ Shared Y-Axis]
   ├── "Right (Comparison)" label       ← orange accent
   └── m_RightGraphController.Root      ← compact graph
   ```

4. **Pair Graph Selection** — checkbox between graphs (matching Profile Analyzer):
   ```csharp
   Toggle m_PairGraphSelection;
   ```
   - When enabled: drag-select on one graph syncs to the other using **normalized percentage mapping** (not frame index). Selecting viewport positions 0.3–0.7 on Left highlights 0.3–0.7 on Right, regardless of frame count differences.
   - When disabled: each graph has independent selection
   - Marker list overlay shows on BOTH graphs regardless of pair toggle

5. **Sub-range re-comparison** — drag-select triggers re-comparison:
   - `OnCompareLeftDragCompleted(startFrame, endFrame)`:
     1. Rebuild left sub-range groups from `m_LeftFrameStore` cache
     2. Re-run `BuildComparison()` with updated left groups vs current right groups
     3. Refresh marker list and right panel
   - If Pair Selection on: both graphs sub-range together → both rebuild → re-compare

6. **Shared Y-axis option** — toggle in options row:
   - When enabled: `Math.Max(leftAutoMax, rightAutoMax)` as Y-axis max for both
   - Add `SetSharedYAxisMax(long max)` to `PerFrameGraphController`
   - Default: off

7. **Graph label** — add `SetLabel(string)` to `PerFrameGraphController`:
   - Adds prefix label to graph header row (e.g., "Left (Baseline)")
   - Optional, no-op when null/empty (Single mode unaffected)

8. **Keyboard focus** — last-clicked graph receives WASD input. When Pair Selection is active, WASD applies to both graphs simultaneously. `GraphElement.focusable` already handles per-element focus.

9. **Tooltips** — both `m_LeftGraphController.TooltipElement` and `m_RightGraphController.TooltipElement` must be added to `rootVisualElement` (via window callback).

10. **State persistence** — `CompareControllerState` includes both graph states:
    ```csharp
    public GraphControllerState LeftGraphState;
    public GraphControllerState RightGraphState;
    ```

### What Doesn't Change
- Single mode graph (`m_GraphController`) untouched
- `GraphElement` rendering unchanged
- `PerFrameGraphController` internal logic unchanged

### Definition of Done
- Compare mode shows two compact stacked graphs with "Left" / "Right" labels
- Each graph has all Single mode capabilities
- Drag-select sub-range re-runs comparison with updated groups
- Pair Selection syncs via normalized percentage mapping
- Shared Y-axis makes bar heights comparable
- State persists through domain reload

### Task-Specific Review Criteria
- [ ] `k_GraphHeight` changed to `readonly float m_GraphHeight` — Single mode constructor passes no arg (default 120f)
- [ ] Single mode's `PerFrameGraphController` construction is unchanged (verify diff)
- [ ] `SetSelection()` has `suppressEvents` guard to prevent infinite Left↔Right recursion
- [ ] Pair Selection uses normalized percentage mapping (full-range-based, not viewport-relative)
- [ ] `AutoYAxisMax` property is read-only; `SetSharedYAxisMax` only overrides auto max, not user zoom
- [ ] Sub-range re-comparison calls `AnalysisEngine.RebuildFromCacheCore()` (not window methods)
- [ ] Both tooltip elements added to `rootVisualElement`
- [ ] Graph compact mode only reduces height — all capabilities (zoom, pan, WASD, segments, overlay) still work
- [ ] Both `GraphControllerState` instances persisted in `CompareControllerState`
- [ ] Frame selection callback is guarded for loaded snapshots (no Profiler navigation when data is from file)

### Commit
```
feat: add paired per-frame graphs for compare mode (Task 12)
```

---

## Task 13: Compare Right Panel (L/R/Diff Summary + Regressions/Improvements)

**Goal:** Right panel shows a three-column summary grid, Top Regressions, Top Improvements, selected ComparedGroup detail, and compare CSV export.

**Files:**
- Modify: `Editor/CompareController.cs`
- Modify: `Editor/GCAllocExporter.cs` — add compare export

### What Changes

1. **`BuildCompareRightPanel()`**:
   ```
   CompareRightPanel (ScrollView)
   ├── Frame Summary foldout (L/R/Diff grid)
   ├── No-data placeholder
   ├── Top Regressions foldout (top 10 by positive delta in primary stat)
   ├── Top Improvements foldout (top 10 by negative delta in primary stat)
   └── Marker Summary foldout (selected ComparedGroup detail)
       ├── Site name + source
       ├── Full L/R/Diff stat breakdown (all per-frame stats)
       ├── Top 3 by frame costs (horizontal bars)
       └── Call stack
   ```

2. **Frame Summary grid** (L/R/Diff) — matching Profile Analyzer:
   ```
                        Left         Right        Diff
   Frame Count:        262          262          0
   Start:              184          184
   End:                445          445
   Max:                4.8 KB       3.2 KB       -1.6 KB
   Median:             1.2 KB       896 B        -328 B
   Mean:               1.4 KB       1.0 KB       -412 B
   Min:                64 B         32 B         -32 B
   Total:              48.2 KB      31.7 KB      -16.5 KB
   Unique Sites:       156          142          -14
   ```
   Diff column uses green/red coloring.

3. **Top Regressions** — sorted by delta in primary stat descending, top 10 positive:
   - Each row: clickable label `DisplayName — +X.X KB (+Y.Y%)`
   - Click → select in compare marker list

4. **Top Improvements** — sorted ascending, top 10 negative:
   - Each row: `DisplayName — -X.X KB (-Y.Y%)`

5. **Marker Summary** (selected ComparedGroup detail):
   ```
   ┌──────────────────────────────────────────────┐
   │ String.Concat  —  MyScript.cs:42             │
   │                                              │
   │              Left        Right       Diff    │
   │ Total:       12.4 KB     8.1 KB     -4.3 KB │
   │ Count:       48          31         -17      │
   │ Max:         1.2 KB      512 B      -700 B   │
   │ Median:      256 B       256 B      0 B      │
   │ Mean:        264 B       268 B      +4 B     │
   │ Min:         64 B        64 B       0 B      │
   │                                              │
   │ Top 3 by frame costs                         │
   │ ████████░░░  1.2 KB  ██████░░░  800 B  -400B │
   │ ██████░░░░░  768 B   █████░░░░  640 B  -128B │
   │                                              │
   │ Call Stack                                   │
   │ ├─ String.Concat (mscorlib)                  │
   │ └─ MyScript.Update() — MyScript.cs:42        │
   └──────────────────────────────────────────────┘
   ```
   - Full per-frame stat breakdown with L/R/Diff (ALL stats shown here — marker list shows only primary stat)
   - **Top 3 by frame costs**: horizontal bars using `VisualElement` with percentage-based widths + background colors (not Painter2D). Both L/R bars share scale = `max(LeftMax, RightMax)`.
   - Reuse `BuildCallStackDisplay()` pattern from single mode
   - All diff values use green/red coloring via `FormatSignedBytes()`

6. **Compare CSV export** — add to `GCAllocExporter`:
   ```csharp
   public static void ExportCompareTableCSV(List<ComparedGroup> groups, CompareStatMode statMode)
   ```
   Columns: Name, Left [stat], Right [stat], Diff, Diff%, Count L, Count R, Count Diff

7. **Export button** in compare right panel header or toolbar context menu.

### What Doesn't Change
- Single mode right panel unchanged
- Existing `UpdateDataSummary()`, `UpdateMarkerSummary()`, `PopulateTopOffendersUI()` unchanged

### Definition of Done
- Compare right panel shows L/R/Diff summary grid
- Top Regressions and Improvements populated and clickable
- Selected ComparedGroup shows side-by-side stats + horizontal bars + call stack
- Diffs are color-coded
- Compare CSV export works

### Task-Specific Review Criteria
- [ ] Right panel layout matches `BuildRightPanel()` pattern (ScrollView, foldouts, same styling)
- [ ] `MakeSectionFoldout()` reused from window (not duplicated)
- [ ] Summary grid labels use pre-computed strings (not formatted during every UI update)
- [ ] Top Regressions/Improvements use clickable labels that select in marker list (no closures in click handler)
- [ ] Horizontal bars use `VisualElement` width percentages (not Painter2D) — matches plan spec
- [ ] Call stack display reuses `BuildCallStackDisplay()` pattern from Single mode
- [ ] `FormatSignedBytes()` used consistently for all diff values
- [ ] `ExportCompareTableCSV` follows `ExportMarkerTableCSV` pattern (static, uses `EditorUtility.SaveFilePanel`)
- [ ] Export uses reusable `StringBuilder`, `GCAllocUtils.EscapeCsvField()` for all string fields
- [ ] Single mode's `UpdateDataSummary()`, `UpdateMarkerSummary()`, `PopulateTopOffendersUI()` have ZERO changes

### Commit
```
feat: add compare right panel with summary, regressions, detail (Task 13)
```

---

## Execution Order

```
Task 7.5 →  Task 8  →  Task 9  →  Task 10  →  Task 11  →  Task 12  →  Task 13
(refactor)  (tabs +    (data)     (toolbar)    (markers)    (graphs)    (right panel)
             skeleton)
```

Each task is independently committable. Each task follows the Quality Gate Process above.

---

## Key Design Decisions (vs Original Plan)

| Issue | Original Plan | Revised |
|-------|--------------|---------|
| Code organization | All in window (~5000 lines) | `CompareController` class (~1500 lines) |
| Toolbar density | Full toolbar per row (20+ controls) | Compact `Pull\|Save\|Load\|status` per row |
| Graph layout | Stacked, full height (200px each) | Stacked, compact (~70px each) matching PA |
| Pair selection sync | Frame index mapping | Normalized percentage mapping |
| `ComparedGroup` strings | `string[5]` indexed by enum | Named fields for active stat mode only |
| `DeltaPercent` | Missing | Added to `ComparedGroup` |
| `BuildComparison` source | `AnalysisSnapshot` (NonSerialized) | `GraphFrameStore` caches (serialized) |
| `GroupByCallsite` | Ambiguous per-side | Single shared toggle |
| Compare trigger | Explicit [Compare] only | Auto-trigger + manual [Compare] button |
| Ratio mode switch | No recomputation | Re-runs `BuildComparison()` |
| Compare export | Not covered | Added `ExportCompareTableCSV` |
| Keyboard focus | Not addressed | Last-clicked graph, or both when paired |
| `FormatSignedBytes` | Not addressed | New utility for diff columns |
| Swap L/R | Missing | [Swap ↔] button in toolbar |
| Serialization | Direct `[SerializeField]` | `CompareControllerState` struct pattern |

---

## MEMORY.md Update Requirements

After each task commit, update `C:\Users\ericj\.claude\projects\c--Users-ericj-repos-GCAllocAnalyzer\memory\MEMORY.md` with:

1. **Task completion status** — which task was completed, on which commit
2. **Architectural decisions** — any decisions made during implementation that deviate from or refine the plan
3. **Patterns established** — new patterns introduced that future tasks should follow
4. **Issues encountered** — problems found during review and how they were resolved

Keep entries concise. Remove stale information. This is the persistent context for future sessions.

---

## Architecture Reference (for Code Reviewer)

The code reviewer must verify new code matches these existing patterns. Reference files:

| Pattern | Reference File | Lines | What to Match |
|---------|---------------|-------|---------------|
| MultiColumnListView setup | `GCAllocAnalyzerWindow.cs` | 726-784 | Virtualization, sort mode, column definitions |
| Bind method pattern | `GCAllocAnalyzerWindow.cs` | 2821-2935 | userData, no closures, unbind cleanup |
| Context menu pattern | `GCAllocAnalyzerWindow.cs` | 2937-2962 | `ContextualMenuManipulator`, not `RegisterCallback` |
| Filter controls | `GCAllocAnalyzerWindow.cs` | 641-716 | TextField setup, non-capturing callbacks |
| Right panel layout | `GCAllocAnalyzerWindow.cs` | 1142-1287 | Foldouts, summary labels, detail view |
| Graph controller wiring | `GCAllocAnalyzerWindow.cs` | 790-798 | Constructor, event subscription, tooltip placement |
| State serialization | `GCAllocAnalyzerData.cs` | 412-441 | `GraphControllerState` struct, `Default` factory |
| Reusable buffers | `GCAllocAnalyzerWindow.cs` | 1472-1482 | Clear-and-reuse pattern, no `new` in hot paths |
| Section foldout styling | `GCAllocAnalyzerWindow.cs` | 3579-3598 | `MakeSectionFoldout()` utility |
| CSV export | `GCAllocExporter.cs` | 16-103 | Static methods, SaveFilePanel, StringBuilder reuse |

---

## Verification (End-to-End)

After all tasks are complete:

1. Open GC Alloc Analyzer window
2. **Regression check**: Single mode works identically to before
3. Switch to Compare mode
4. Left side: Pull Data from Profiler
5. Right side: Load a previously saved snapshot
6. Verify:
   - Both compact graphs render with correct data
   - Marker list shows delta columns, sorted by |Diff| by default
   - Regressions are red, improvements are green
   - Summary grid shows correct L/R/Diff values
   - Top Regressions/Improvements lists are populated
   - Click a marker → overlay on both graphs + detail panel
   - Change Marker Columns dropdown → columns update
   - Change Ratio mode → deltas recompute
   - Toggle shared Y-axis → bars rescale
   - Pair Graph Selection + drag-select → both graphs sync by percentage
   - Domain reload preserves mode, both snapshots, graph state
   - Save/Load works independently on each side
   - [Swap ↔] swaps left/right correctly
   - Filters (name, exclude, group-by) work in compare mode
   - Compare CSV export produces correct delta table
   - Keyboard WASD works on focused graph
