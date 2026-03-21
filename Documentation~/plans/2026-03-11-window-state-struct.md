# WindowState Struct Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace scattered save/restore/validate logic with a centralized `WindowState` struct, a single `CaptureWindowState()`, a single `ApplyWindowState()`, and a `ValidateState()` that compares live state against the struct.

**Architecture:** All user-visible state that must survive domain reload lives in one `[Serializable] struct WindowState`. OnDisable calls `CaptureWindowState()` to snapshot everything. After all rebuilds complete (skeleton phase and allocs phase), `ApplyWindowState()` applies the saved state in the correct order. `ValidateState()` re-captures live state and compares field-by-field against the saved struct. Adding a new persistent field means touching exactly three places: the struct, ApplyWindowState, and ValidateState.

**Tech Stack:** C# 9.0, Unity 6000.0+ UIElements, `[SerializeField]` serialization

---

## Current State Inventory

These serialized fields on GCAllocAnalyzerWindow currently hold user-visible state:

| Field | Type | Category |
|-------|------|----------|
| `m_GraphState` | `GraphControllerState` | Graph (viewport, Y-axis, frame selection) |
| `m_NameFilterText` | `string` | Filter |
| `m_ExcludeFilterText` | `string` | Filter |
| `m_GroupByCallsiteValue` | `bool` | Filter |
| `m_SerializedSelectedThreads` | `string[]` | Filter |
| `m_SortCol` | `SortCol` | Sort |
| `m_SortAsc` | `bool` | Sort |
| `m_SelectedMarkerIndex` | `int` | Selection |
| `m_SelectedAllocIndex` | `int` | Selection |
| `m_ShowAssembly` | `bool` | Display |
| `m_IsLoadedSnapshot` | `bool` | Display |

Fields NOT in the struct (infrastructure, not user state):
- `m_Snapshot`, `m_FrameStore` — analysis data, serialized separately
- `m_SnapshotFilePath` — file path for .gcas restore

---

### Task 1: Define WindowState struct in GCAllocAnalyzerData.cs

**Files:**
- Modify: `Editor/GCAllocAnalyzerData.cs` (after GraphControllerState, before closing `}`)

**Step 1: Add the WindowState struct**

Add this after the `GraphControllerState` struct (after line 483):

```csharp
// ═══════════════════════════════════════════════════
//  WindowState — all user-visible state that must survive
//  domain reload, captured as a single unit.
// ═══════════════════════════════════════════════════

[Serializable]
internal struct WindowState
{
    // Graph
    public GraphControllerState Graph;

    // Filters
    public string NameFilter;
    public string ExcludeFilter;
    public bool GroupByCallsite;
    public string[] SelectedThreads;  // empty = all threads

    // Sort
    public int SortCol;    // cast from SortCol enum (private to window)
    public bool SortAsc;

    // Selections
    public int SelectedMarkerIndex;
    public int SelectedAllocIndex;

    // Display
    public bool ShowAssembly;
    public bool IsLoadedSnapshot;

    public static WindowState Default => new WindowState
    {
        Graph = GraphControllerState.Default,
        NameFilter = "",
        ExcludeFilter = "",
        GroupByCallsite = true,
        SelectedThreads = Array.Empty<string>(),
        SortCol = 0,  // Bytes
        SortAsc = false,
        SelectedMarkerIndex = -1,
        SelectedAllocIndex = -1,
        ShowAssembly = false,
        IsLoadedSnapshot = false
    };
}
```

**Step 2: Build**

Run: `dotnet build c:/Users/ericj/repos/GCAllocAnalyzer-Dev/GCAllocBreakdown.Editor.csproj`
Expected: Build succeeded, 0 errors

**Step 3: Commit**

```
git add Editor/GCAllocAnalyzerData.cs
git commit -m "Add WindowState struct for centralized domain reload state"
```

---

### Task 2: Add CaptureWindowState() and replace OnDisable field captures

**Files:**
- Modify: `Editor/GCAllocAnalyzerWindow.cs`

**Step 1: Replace scattered serialized fields with single WindowState field**

Find the block of serialized user-state fields (lines ~133–142):

```csharp
[SerializeField] SortCol m_SortCol = SortCol.Bytes;
[SerializeField] bool m_SortAsc;
[SerializeField] bool m_ShowAssembly;
[SerializeField] GraphControllerState m_GraphState = GraphControllerState.Default;
[SerializeField] string m_NameFilterText = "";
[SerializeField] string m_ExcludeFilterText = "";
[SerializeField] bool m_GroupByCallsiteValue = true;
[SerializeField] string[] m_SerializedSelectedThreads = Array.Empty<string>();
[SerializeField] int m_SelectedMarkerIndex = -1;
[SerializeField] int m_SelectedAllocIndex = -1;
```

Replace with:

```csharp
[SerializeField] WindowState m_SavedState = WindowState.Default;
```

Keep these — they are NOT user state:
- `[SerializeField] bool m_IsLoadedSnapshot;` — move INTO WindowState reads/writes
- `[SerializeField] AnalysisSnapshot m_Snapshot = new();`
- `[SerializeField] GraphFrameStore m_FrameStore = new();`
- `[SerializeField] string m_SnapshotFilePath;`

**Step 2: Add CaptureWindowState() method**

Add near OnDisable:

```csharp
WindowState CaptureWindowState()
{
    var state = new WindowState
    {
        Graph = m_GraphController.CaptureState(),
        NameFilter = m_NameFilter.value,
        ExcludeFilter = m_ExcludeFilter.value,
        GroupByCallsite = m_GroupByCallsite.value,
        SortCol = (int)m_SortCol,
        SortAsc = m_SortAsc,
        SelectedMarkerIndex = m_MarkerListView.selectedIndex,
        SelectedAllocIndex = m_AllocListView.selectedIndex,
        ShowAssembly = m_ShowAssembly,
        IsLoadedSnapshot = m_IsLoadedSnapshot
    };

    // Serialize thread selection (HashSet not serializable).
    // Empty array = "all threads" (no filtering active).
    if (m_SelectedThreads.Count > 0 && m_SelectedThreads.Count < m_AllThreadNames.Count)
    {
        state.SelectedThreads = new string[m_SelectedThreads.Count];
        int idx = 0;
        foreach (string t in m_SelectedThreads)
            state.SelectedThreads[idx++] = t;
    }
    else
    {
        state.SelectedThreads = Array.Empty<string>();
    }

    return state;
}
```

**Step 3: Simplify OnDisable**

Replace the field-by-field capture block in OnDisable with:

```csharp
m_SavedState = CaptureWindowState();
```

Remove the individual field assignments and the thread serialization block — they're now inside `CaptureWindowState()`.

**Step 4: Update all references to the old field names**

Every place that reads from the old serialized fields must now read from `m_SavedState.*`:

| Old | New |
|-----|-----|
| `m_GraphState` | `m_SavedState.Graph` |
| `m_NameFilterText` | `m_SavedState.NameFilter` |
| `m_ExcludeFilterText` | `m_SavedState.ExcludeFilter` |
| `m_GroupByCallsiteValue` | `m_SavedState.GroupByCallsite` |
| `m_SerializedSelectedThreads` | `m_SavedState.SelectedThreads` |
| `m_SortCol` | `(SortCol)m_SavedState.SortCol` |
| `m_SortAsc` | `m_SavedState.SortAsc` |
| `m_SelectedMarkerIndex` | `m_SavedState.SelectedMarkerIndex` |
| `m_SelectedAllocIndex` | `m_SavedState.SelectedAllocIndex` |
| `m_ShowAssembly` | `m_SavedState.ShowAssembly` |
| `m_IsLoadedSnapshot` | `m_SavedState.IsLoadedSnapshot` |

**Important**: `m_SortCol` and `m_SortAsc` are also READ during normal operation (not just restore). They remain as local fields for runtime use but are captured into/restored from `m_SavedState`. Same for `m_ShowAssembly` and `m_IsLoadedSnapshot`. These fields still exist as working copies — the struct is the serialization format.

So keep these as working fields (NOT `[SerializeField]`):

```csharp
SortCol m_SortCol = SortCol.Bytes;
bool m_SortAsc;
bool m_ShowAssembly;
bool m_IsLoadedSnapshot;
```

And in the restore path, copy from struct back to working fields:

```csharp
m_SortCol = (SortCol)m_SavedState.SortCol;
m_SortAsc = m_SavedState.SortAsc;
m_ShowAssembly = m_SavedState.ShowAssembly;
m_IsLoadedSnapshot = m_SavedState.IsLoadedSnapshot;
```

**Step 5: Build**

Run: `dotnet build c:/Users/ericj/repos/GCAllocAnalyzer-Dev/GCAllocBreakdown.Editor.csproj`
Expected: Build succeeded, 0 errors

**Step 6: Commit**

```
git add Editor/GCAllocAnalyzerWindow.cs
git commit -m "Replace scattered serialized fields with CaptureWindowState into WindowState struct"
```

---

### Task 3: Add ApplyWindowState() and consolidate restore logic

**Files:**
- Modify: `Editor/GCAllocAnalyzerWindow.cs`

**Step 1: Create ApplyWindowState()**

This method applies the saved state in the correct order AFTER all rebuilds. It replaces the scattered restore code currently in TryRestoreAfterReload and OnAllocsRestoredFromFile.

```csharp
/// <summary>
/// Apply saved user-visible state after all data rebuilds are complete.
/// Call order matters: filters → graph → selections (each depends on prior).
/// </summary>
void ApplyWindowState(bool hasAllocs)
{
    ref readonly var state = ref m_SavedState;

    // ── 1. Working field copies ──
    m_SortCol = (SortCol)state.SortCol;
    m_SortAsc = state.SortAsc;
    m_ShowAssembly = state.ShowAssembly;
    m_IsLoadedSnapshot = state.IsLoadedSnapshot;

    // ── 2. Thread filter ──
    m_SelectedThreads.Clear();
    for (int i = 0; i < state.SelectedThreads.Length; i++)
    {
        if (m_AllThreadNames.Contains(state.SelectedThreads[i]))
            m_SelectedThreads.Add(state.SelectedThreads[i]);
    }
    UpdateThreadButtonLabel();

    // ── 3. Filters + sort ──
    // UI fields already bound via [SerializeField] on Toggle/TextField,
    // but ApplyFilters reads m_SelectedThreads which we just restored.
    ApplyFilters();
    RestoreMarkerSortIndicator();

    // ── 4. Graph state (viewport + Y-axis) ──
    m_GraphController.RestoreState(state.Graph);
    m_GraphController.RebuildGraph();

    // ── 5. Marker selection (ApplyFilters defaults to index 0) ──
    if (state.SelectedMarkerIndex >= 0 && state.SelectedMarkerIndex < m_FilteredGroups.Count)
    {
        m_MarkerListView.selectedIndex = state.SelectedMarkerIndex;
        UpdateMarkerSummary(m_FilteredGroups[state.SelectedMarkerIndex]);
    }

    // ── 6. Alloc selection (only after allocs phase) ──
    if (hasAllocs && state.SelectedAllocIndex >= 0
        && state.SelectedAllocIndex < m_SelectedAllocations.Count)
    {
        m_AllocListView.selectedIndex = state.SelectedAllocIndex;
    }

    // ── 7. Loaded snapshot label ──
    if (m_IsLoadedSnapshot)
        m_LoadedSnapshotLabel.style.display = DisplayStyle.Flex;

    // ── 8. Frame range UI ──
    m_StartFrameField.value = GCAllocUtils.DisplayFrame(m_Snapshot.FrameStart);
    m_EndFrameField.value = GCAllocUtils.DisplayFrame(m_Snapshot.FrameEnd);
    UpdateFrameRangeInfo();
}
```

**Step 2: Simplify TryRestoreAfterReload — remove scattered restore code**

After the data rebuild section (BuildTopOffenders, ApplyFilters, etc.), replace all the individual restore calls with:

```csharp
ApplyWindowState(false);  // skeleton phase — no allocs yet
```

Remove from TryRestoreAfterReload:
- Thread selection restore (lines 472–478)
- `UpdateThreadButtonLabel()` (line 480)
- Marker selection restore (lines 489–491)
- `RebuildGraph()` + `RestoreState` + `RebuildGraph` (lines 493–495)
- Frame range UI restore (lines 497–500)
- `RestoreMarkerSortIndicator()` (line 511)
- Loaded snapshot label (lines 515–516)

Keep:
- Skeleton file read, group list population, frame store restore
- `m_ActiveGroups` assignment, `BuildTopOffenders()`, initial `ApplyFilters()`, `UpdateDataSummary()`, `ShowNoDataState()`
- The `RebuildGraph()` call (needed for segment data) — but move `RestoreState`/`RebuildGraph` into `ApplyWindowState`

**Important**: `TryRestoreAfterReload` still needs its own `RebuildGraph()` call before `ApplyWindowState` because `SetData` must run to build segment data. The `ApplyWindowState` then restores viewport/Y-axis via `RestoreState` + `RebuildGraph` on the controller.

Resulting TryRestoreAfterReload structure:

```
1. Read skeleton from file
2. Populate groups, frame store
3. Rebuild threads, active groups, top offenders
4. ApplyFilters + UpdateDataSummary + ShowNoDataState
5. RebuildGraph()                    ← SetData for segment data
6. ApplyWindowState(false)           ← restores everything in correct order
7. Status label, disable save/export
8. ValidateState("skeleton")
9. Queue background alloc restore
```

**Step 3: Simplify OnAllocsRestoredFromFile — remove scattered restore code**

After the data rebuild section, replace all individual restore calls with:

```csharp
ApplyWindowState(true);  // allocs phase — full restore including alloc selection
```

Remove from OnAllocsRestoredFromFile:
- Thread filter restore block (lines 732–743)
- Graph state restore (lines 748–749)
- Marker selection restore (lines 751–758)
- Alloc selection restore (lines 760–762)

Keep:
- Sub-range detection, full-range setup
- `EnsureGroupingIds`, `RebuildThreadAllocCounts`, `UpdateThreadButtonLabel`
- `m_ThreadIndexNames` rebuild
- Frame store cache rebuild
- `ComputeSnapshotPerFrameBytes`, `FullFrameBytes` copy
- `BuildTopOffenders`, `BuildThreadIndex`
- `RebuildGraph()`, `RebuildFromCache` (if sub-range)

Resulting OnAllocsRestoredFromFile structure:

```
1. Set RawAllocations, detect sub-range
2. Full-range setup (if sub-range)
3. Rebuild alloc-dependent state (grouping IDs, thread counts, etc.)
4. Rebuild frame store caches
5. ComputeSnapshotPerFrameBytes, FullFrameBytes
6. BuildTopOffenders, BuildThreadIndex
7. RebuildGraph()                    ← SetData for segment data
8. RebuildFromCache (if sub-range)   ← clobbers threads, markers, etc.
9. ApplyWindowState(true)            ← restores everything in correct order
10. Enable save/export, status label
11. ValidateState("allocs")
```

**Step 4: Build**

Run: `dotnet build c:/Users/ericj/repos/GCAllocAnalyzer-Dev/GCAllocBreakdown.Editor.csproj`
Expected: Build succeeded, 0 errors

**Step 5: Commit**

```
git add Editor/GCAllocAnalyzerWindow.cs
git commit -m "Consolidate restore logic into single ApplyWindowState method"
```

---

### Task 4: Rewrite ValidateState to compare against WindowState struct

**Files:**
- Modify: `Editor/GCAllocAnalyzerWindow.cs`

**Step 1: Rewrite ValidateState**

Replace the current ValidateState with a version that compares live state against `m_SavedState`:

```csharp
void ValidateState(string phase)
{
    if (!m_Snapshot.HasData) return;

    var sb = new StringBuilder(512);
    bool ok = true;

    void Fail(string msg) { ok = false; sb.Append("  FAIL: "); sb.Append(msg); sb.Append('\n'); }

    ref readonly var expected = ref m_SavedState;

    // ── Structural invariants (always) ──
    if (m_Snapshot.GroupsByFullCallstack.Count == 0) Fail("GroupsByFullCallstack empty");
    if (m_Snapshot.GroupsByTopFrame.Count == 0) Fail("GroupsByTopFrame empty");
    if (m_ActiveGroups == null) Fail("m_ActiveGroups null");
    if (m_GraphController == null) Fail("m_GraphController null");
    if (m_FrameStore.FullFrameBytes == null) Fail("FullFrameBytes null");
    else
    {
        int expectedLen = m_FrameStore.FullFrameEnd - m_FrameStore.FullFrameStart + 1;
        if (m_FrameStore.FullFrameBytes.Length != expectedLen)
            Fail($"FullFrameBytes.Length ({m_FrameStore.FullFrameBytes.Length}) != expected ({expectedLen})");
    }

    // ── User-visible state (live vs saved WindowState) ──

    // Filters
    if (m_NameFilter.value != expected.NameFilter)
        Fail($"NameFilter: UI='{m_NameFilter.value}' expected='{expected.NameFilter}'");
    if (m_ExcludeFilter.value != expected.ExcludeFilter)
        Fail($"ExcludeFilter: UI='{m_ExcludeFilter.value}' expected='{expected.ExcludeFilter}'");
    if (m_GroupByCallsite.value != expected.GroupByCallsite)
        Fail($"GroupByCallsite: UI={m_GroupByCallsite.value} expected={expected.GroupByCallsite}");

    // Sort
    if ((int)m_SortCol != expected.SortCol)
        Fail($"SortCol: live={(int)m_SortCol} expected={expected.SortCol}");
    if (m_SortAsc != expected.SortAsc)
        Fail($"SortAsc: live={m_SortAsc} expected={expected.SortAsc}");

    // Thread filter
    if (expected.SelectedThreads.Length > 0)
    {
        if (m_SelectedThreads.Count != expected.SelectedThreads.Length)
            Fail($"ThreadFilter: {m_SelectedThreads.Count} selected, expected {expected.SelectedThreads.Length}");
    }

    // Marker selection
    if (expected.SelectedMarkerIndex >= 0 && m_FilteredGroups.Count > 0
        && expected.SelectedMarkerIndex < m_FilteredGroups.Count)
    {
        if (m_MarkerListView.selectedIndex != expected.SelectedMarkerIndex)
            Fail($"SelectedMarker: UI={m_MarkerListView.selectedIndex} expected={expected.SelectedMarkerIndex}");
    }

    // Alloc selection (allocs phase only)
    if (phase == "allocs" && expected.SelectedAllocIndex >= 0
        && m_SelectedAllocations.Count > 0
        && expected.SelectedAllocIndex < m_SelectedAllocations.Count)
    {
        if (m_AllocListView.selectedIndex != expected.SelectedAllocIndex)
            Fail($"SelectedAlloc: UI={m_AllocListView.selectedIndex} expected={expected.SelectedAllocIndex}");
    }

    // Graph state
    if (m_GraphController != null)
    {
        var live = m_GraphController.CaptureState();
        ref readonly var eg = ref expected.Graph;
        if (Mathf.Abs(live.ViewportStart - eg.ViewportStart) > 0.001f)
            Fail($"ViewportStart: live={live.ViewportStart:F3} expected={eg.ViewportStart:F3}");
        if (Mathf.Abs(live.ViewportEnd - eg.ViewportEnd) > 0.001f)
            Fail($"ViewportEnd: live={live.ViewportEnd:F3} expected={eg.ViewportEnd:F3}");
        if (live.OrderByMagnitude != eg.OrderByMagnitude)
            Fail($"OrderByMagnitude: live={live.OrderByMagnitude} expected={eg.OrderByMagnitude}");
        if (live.HasCustomYScale != eg.HasCustomYScale)
            Fail($"HasCustomYScale: live={live.HasCustomYScale} expected={eg.HasCustomYScale}");
        if (live.HasCustomYScale && eg.HasCustomYScale)
        {
            if (live.UserYAxisMax != eg.UserYAxisMax)
                Fail($"UserYAxisMax: live={live.UserYAxisMax} expected={eg.UserYAxisMax}");
            if (live.YPanOffset != eg.YPanOffset)
                Fail($"YPanOffset: live={live.YPanOffset} expected={eg.YPanOffset}");
        }
        if (live.HasFrameSelection != eg.HasFrameSelection)
            Fail($"HasFrameSelection: live={live.HasFrameSelection} expected={eg.HasFrameSelection}");
    }

    // Display
    if (m_ShowAssembly != expected.ShowAssembly)
        Fail($"ShowAssembly: live={m_ShowAssembly} expected={expected.ShowAssembly}");

    // ── Alloc-phase structural invariants ──
    if (phase == "allocs")
    {
        if (m_Snapshot.RawAllocations.Count != m_Snapshot.TotalCount)
            Fail($"RawAllocations.Count ({m_Snapshot.RawAllocations.Count}) != TotalCount ({m_Snapshot.TotalCount})");
        if (m_ThreadIndexNames.Count != m_Snapshot.SortedThreadNames.Count)
            Fail($"ThreadIndexNames ({m_ThreadIndexNames.Count}) != SortedThreadNames ({m_Snapshot.SortedThreadNames.Count})");
        if (m_FrameStore.CachedRawAllocations == null || m_FrameStore.CachedRawAllocations.Count == 0)
            Fail("CachedRawAllocations empty");
        if (m_Snapshot.PerFrameBytes == null) Fail("PerFrameBytes null");
        if (m_Snapshot.FrameStart < m_FrameStore.FullFrameStart)
            Fail($"FrameStart ({m_Snapshot.FrameStart}) < FullFrameStart ({m_FrameStore.FullFrameStart})");
        if (m_Snapshot.FrameEnd > m_FrameStore.FullFrameEnd)
            Fail($"FrameEnd ({m_Snapshot.FrameEnd}) > FullFrameEnd ({m_FrameStore.FullFrameEnd})");

        if (m_GraphController != null)
        {
            var live = m_GraphController.CaptureState();
            if (live.HasFrameSelection && live.SelectedFrameBuffer == null)
                Fail("HasFrameSelection=true but SelectedFrameBuffer is null");
        }
    }

    if (ok)
        Debug.Log($"[GCAllocAnalyzer] ValidateState({phase}): ALL PASS");
    else
        Debug.LogError($"[GCAllocAnalyzer] ValidateState({phase}) FAILURES:\n{sb}");
}
```

**Step 2: Build**

Run: `dotnet build c:/Users/ericj/repos/GCAllocAnalyzer-Dev/GCAllocBreakdown.Editor.csproj`
Expected: Build succeeded, 0 errors

**Step 3: Commit**

```
git add Editor/GCAllocAnalyzerWindow.cs
git commit -m "Rewrite ValidateState to compare live state against WindowState struct"
```

---

### Task 5: Verify — domain reload smoke test

**Manual test in Unity Editor:**

1. Open GC Alloc Analyzer, run an analysis (100+ frames)
2. Set up state to test:
   - Type a name filter (e.g., "LINQ")
   - Type an exclude filter (e.g., "String")
   - Select a specific thread in the thread dropdown
   - Select a marker deep in the list (not index 0)
   - Click an individual allocation (not index 0)
   - Zoom into the X viewport (mouse wheel on graph)
   - Zoom into the Y axis (scroll wheel on Y-axis)
   - Drag-select a sub-range on the graph
   - Toggle "Order by Size"
3. Save a C# script to trigger domain reload
4. Check console output:
   - `ValidateState(skeleton): ALL PASS`
   - `ValidateState(allocs): ALL PASS`
5. Visually confirm all state matches what was set before reload

**Step 1: Commit**

```
git add Editor/GCAllocAnalyzerData.cs Editor/GCAllocAnalyzerWindow.cs
git commit -m "WindowState struct refactor complete — centralized save/restore/validate"
```
