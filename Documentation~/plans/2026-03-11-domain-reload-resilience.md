# Domain Reload Resilience — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make every user-visible state survive domain reload without crashes, stale data, or missing UI.

**Architecture:** Unity EditorWindow serialization preserves `[SerializeField]` fields but drops everything else. The window uses a two-phase restore: skeleton (instant, synchronous) from a `.gcas` file, then raw allocations (background thread). Every piece of non-serialized state must be rebuilt from serialized state or from the `.gcas` file during one of these two phases. This plan audits every field, fixes all known mismatches, and ensures the rebuild chain is complete.

**Tech Stack:** Unity 6000.0+, C# 9.0, UIElements, `System.Threading.ThreadPool`

**Build command:** `dotnet build c:/Users/ericj/repos/GCAllocAnalyzer-Dev/GCAllocBreakdown.Editor.csproj`

**No automated tests.** Manual testing via Play Mode allocation generators + domain reload triggers (save a script).

---

## Context: Domain Reload Flow

```
Domain reload triggered (script save / enter Play Mode)
  └─ OnDisable()             ← capture GraphControllerState, serialize threads, spin-wait/skip write
     └─ Unity serializes [SerializeField] fields
        └─ Unity destroys managed state
           └─ Unity reconstructs managed object (C# constructor runs → readonly initializers)
              └─ Unity deserializes [SerializeField] fields
                 └─ CreateGUI()
                    ├─ BuildToolbar()      ← UI created, serialized filter text applied
                    ├─ BuildLeftPanel()
                    │   └─ BuildPerFrameGraph()
                    │       ├─ new PerFrameGraphController(...)
                    │       ├─ SetData(m_FrameStore, m_Snapshot, m_FilteredGroups, groupBy)
                    │       └─ RestoreState(m_GraphState)
                    ├─ BuildRightPanel()
                    └─ TryRestoreAfterReload()
                        ├─ ReadSkeleton from .gcas (groups, frame store)
                        ├─ Rebuild threads, filters, marker list, graph
                        └─ Background thread: ReadAllocations
                            └─ delayCall → OnAllocsRestoredFromFile()
                                ├─ Populate CachedRawAllocations
                                ├─ ComputeSnapshotPerFrameBytes
                                ├─ RebuildGraph (with segments)
                                └─ If sub-range: RebuildFromCache
```

## State Inventory

### Serialized (survives reload)

| Field | Type | Notes |
|-------|------|-------|
| `m_Snapshot` | `AnalysisSnapshot` | Partial — scalar fields survive, `[NonSerialized]` lists don't |
| `m_FrameStore` | `GraphFrameStore` | Partial — `FullFrameBytes` survives, `Cached*` fields don't |
| `m_GraphState` | `GraphControllerState` | Partial — scalars survive, `SelectedFrameBuffer` doesn't |
| `m_SortCol`, `m_SortAsc` | sort state | Survives |
| `m_ShowAssembly` | `bool` | Survives |
| `m_NameFilterText`, `m_ExcludeFilterText` | `string` | Survives |
| `m_GroupByCallsiteValue` | `bool` | Survives |
| `m_SerializedSelectedThreads` | `string[]` | Survives |
| `m_SelectedMarkerIndex` | `int` | Survives |
| `m_SnapshotFilePath` | `string` | Survives |
| `m_IsLoadedSnapshot` | `bool` | Survives |

### Non-serialized (rebuilt from above)

| Field | Rebuilt in | Current status |
|-------|-----------|----------------|
| `m_Snapshot.RawAllocations` | `OnAllocsRestoredFromFile` | OK |
| `m_Snapshot.GroupsByFullCallstack/TopFrame` | `TryRestoreAfterReload` (from .gcas) | OK (full-range only) |
| `m_Snapshot.PerFrameBytes` | `OnAllocsRestoredFromFile` | **BUG: uses sub-range FrameStart/End** |
| `m_FrameStore.CachedRawAllocations` | `OnAllocsRestoredFromFile` | OK |
| `m_FrameStore.CachedGroups*` | `OnAllocsRestoredFromFile` | OK |
| `m_GraphState.SelectedFrameBuffer` | `RestoreState` | **FIXED: RebuildFrameSelectionBuffer** |
| `m_ActiveGroups` | `TryRestoreAfterReload` | OK |
| `m_FilteredGroups` | `ApplyFilters` | OK (full-range during skeleton) |
| `m_AllThreadNames` | `TryRestoreAfterReload` | OK |
| `m_SelectedThreads` | `TryRestoreAfterReload` | OK |
| `m_ThreadAllocCounts` | `OnAllocsRestoredFromFile` | OK (empty during skeleton — acceptable) |
| `m_ThreadIndexNames` | **NEVER** | **BUG: empty after reload, breaks sub-range thread counting** |
| `m_GroupThreadIndex` | `OnAllocsRestoredFromFile` | OK (empty during skeleton — thread filter silently ignored, acceptable) |
| `m_GraphController` | `BuildPerFrameGraph` | OK (recreated) |

---

## Issues to Fix

### Issue 1 (CRASH → fixed): `HasFrameSelection` true + `SelectedFrameBuffer` null

**Status:** Already fixed. `RestoreState` guards with `&& state.SelectedFrameBuffer != null` and calls `RebuildFrameSelectionBuffer()` from serialized frame indices.

### Issue 2 (BROKEN): Sub-range not restored after domain reload

**Problem:** When the user had a sub-range selected (drag-select on graph), `m_Snapshot.FrameStart/End` is serialized as the sub-range. But `OnAllocsRestoredFromFile` rebuilds everything for the full range. The marker list shows full-range data instead of the sub-range the user was viewing.

Additionally, `ComputeSnapshotPerFrameBytes()` uses `m_Snapshot.FrameStart/End` (the sub-range values) to size the per-frame array, so it would compute per-frame bytes only for the sub-range when it should compute for the full range first (for `FullFrameBytes` / graph bars).

**Fix:** In `OnAllocsRestoredFromFile`:
1. Save the serialized `FrameStart/End` (may be sub-range)
2. Temporarily set `FrameStart/End` to `FullFrameStart/End`
3. Do all full-range setup (per-frame bytes, FullFrameBytes copy, segments, palette)
4. After full setup, if sub-range was active, call `RebuildFromCache(subStart, subEnd)`

**Files:** `Editor/GCAllocAnalyzerWindow.cs` — `OnAllocsRestoredFromFile` method (~line 526)

### Issue 3 (BROKEN): `m_ThreadIndexNames` never rebuilt after reload

**Problem:** `m_ThreadIndexNames` is populated only during `RunAnalysis` extraction (per-thread scanning). It's a `readonly List<string>` — after reload, it's empty (new). `RebuildFromCache` uses `m_ThreadIndexNames.Count` as `threadNameCount`, which is 0 → the int[]-indexed thread counting loop becomes a no-op → thread filter button shows "0 allocs" for every thread after sub-range selection post-reload.

**Fix:** Rebuild `m_ThreadIndexNames` from `m_Snapshot.SortedThreadNames` in `OnAllocsRestoredFromFile`, before any sub-range rebuild can run.

**Files:** `Editor/GCAllocAnalyzerWindow.cs` — `OnAllocsRestoredFromFile` method

### Issue 4 (ALREADY FIXED): `m_SnapshotDirty` set by sub-range rebuilds

**Status:** Already fixed. Removed `m_SnapshotDirty = true` from `RebuildFromCache` and `RebuildFromCacheWithBuffer`.

### Issue 5 (ALREADY FIXED): Compile errors — `SetData` arg count + `group.Allocations` removed

**Status:** Just fixed. `SetData`/`UpdateAnalyzedRange` now accept `groupByCallsite` bool. `BuildOverlayBars` scans `m_Snapshot.RawAllocations` by group index instead of removed `group.Allocations`.

---

## Tasks

### Task 1: Fix `OnAllocsRestoredFromFile` — full-range setup before sub-range

**Files:**
- Modify: `Editor/GCAllocAnalyzerWindow.cs` — `OnAllocsRestoredFromFile` method (~line 526-575)

**Step 1: Read the current method**

Read `OnAllocsRestoredFromFile` to confirm current state after compile error fixes.

**Step 2: Add sub-range detection and full-range temporary restore**

At the top of `OnAllocsRestoredFromFile`, after `m_Snapshot.RawAllocations = allocs;`:

```csharp
// Detect sub-range: serialized FrameStart/End may differ from full range
bool isSubRange = m_Snapshot.FrameStart != m_FrameStore.FullFrameStart
    || m_Snapshot.FrameEnd != m_FrameStore.FullFrameEnd;
int subRangeStart = m_Snapshot.FrameStart;
int subRangeEnd = m_Snapshot.FrameEnd;

// Temporarily restore full range for graph setup.
// ComputeSnapshotPerFrameBytes and FullFrameBytes need full-range FrameStart/End.
if (isSubRange)
{
    m_Snapshot.FrameStart = m_FrameStore.FullFrameStart;
    m_Snapshot.FrameEnd = m_FrameStore.FullFrameEnd;
}
```

**Step 3: Add FullFrameBytes copy after ComputeSnapshotPerFrameBytes**

After `ComputeSnapshotPerFrameBytes();` (currently line ~545), add:

```csharp
// Copy full-range per-frame bytes to FullFrameBytes (graph bars source)
if (m_FrameStore.FullFrameBytes == null
    || m_FrameStore.FullFrameBytes.Length != m_Snapshot.PerFrameBytes.Length)
{
    int count = m_Snapshot.PerFrameBytes.Length;
    m_FrameStore.FullFrameBytes = new long[count];
    Array.Copy(m_Snapshot.PerFrameBytes, m_FrameStore.FullFrameBytes, count);
}
```

**Step 4: Add sub-range rebuild after full graph setup**

After `m_GraphController.RestoreState(graphState);`, add:

```csharp
// Re-apply sub-range analysis now that the cache is populated
if (isSubRange)
    RebuildFromCache(subRangeStart, subRangeEnd);
```

**Step 5: Build and verify**

Run: `dotnet build c:/Users/ericj/repos/GCAllocAnalyzer-Dev/GCAllocBreakdown.Editor.csproj`
Expected: 0 errors, 0 warnings

**Step 6: Commit**

```
git add Editor/GCAllocAnalyzerWindow.cs
git commit -m "Restore sub-range analysis after domain reload in OnAllocsRestoredFromFile"
```

---

### Task 2: Rebuild `m_ThreadIndexNames` after domain reload

**Files:**
- Modify: `Editor/GCAllocAnalyzerWindow.cs` — `OnAllocsRestoredFromFile` method

**Step 1: Add thread index rebuild**

In `OnAllocsRestoredFromFile`, after `UpdateThreadButtonLabel();` and before the frame store cache setup, add:

```csharp
// Rebuild m_ThreadIndexNames from sorted thread names.
// This list is normally populated during RunAnalysis extraction. After domain
// reload it's empty (readonly initializer). RebuildFromCache uses its Count
// for int[]-indexed thread counting — without this, thread counts are all zero.
m_ThreadIndexNames.Clear();
for (int i = 0; i < m_Snapshot.SortedThreadNames.Count; i++)
    m_ThreadIndexNames.Add(m_Snapshot.SortedThreadNames[i]);
```

Also ensure `m_ThreadCountBuffer` is sized to match:

```csharp
if (m_ThreadCountBuffer == null || m_ThreadCountBuffer.Length < m_ThreadIndexNames.Count)
    m_ThreadCountBuffer = new int[m_ThreadIndexNames.Count];
```

**Step 2: Build and verify**

Run: `dotnet build c:/Users/ericj/repos/GCAllocAnalyzer-Dev/GCAllocBreakdown.Editor.csproj`
Expected: 0 errors, 0 warnings

**Step 3: Commit**

```
git add Editor/GCAllocAnalyzerWindow.cs
git commit -m "Rebuild m_ThreadIndexNames after domain reload for sub-range thread counting"
```

---

### Task 3: Add `ValidateState()` invariant checker

**Goal:** A method that asserts structural consistency of the window state. Runs automatically after every restore path. Catches state mismatches without needing specific test data — just checks that nothing is contradictory.

**Files:**
- Modify: `Editor/GCAllocAnalyzerWindow.cs`

**Invariants to check (two phases):**

**Phase A — After skeleton restore (`TryRestoreAfterReload`):**
These hold before allocations are loaded.

| # | Invariant | Rationale |
|---|-----------|-----------|
| A1 | `HasData` → `GroupsByFullCallstack.Count > 0` | Skeleton loaded groups |
| A2 | `HasData` → `GroupsByTopFrame.Count > 0` | Skeleton loaded groups |
| A3 | `HasData` → `m_ActiveGroups != null` | Assigned from groups |
| A4 | `HasData` → `m_FilteredGroups.Count > 0` (unless filter excludes all) | ApplyFilters ran |
| A5 | `HasData` → `FullFrameBytes != null && FullFrameBytes.Length > 0` | Graph bars source |
| A6 | `HasData` → `FullFrameBytes.Length == FullFrameEnd - FullFrameStart + 1` | Array matches range |

**Phase B — After allocs restore (`OnAllocsRestoredFromFile`):**
Full state should be consistent.

| # | Invariant | Rationale |
|---|-----------|-----------|
| B1 | All of Phase A invariants | Still hold |
| B2 | `RawAllocations.Count == TotalCount` | Allocs fully loaded |
| B3 | `m_ThreadIndexNames.Count == SortedThreadNames.Count` | Thread index rebuilt |
| B4 | `CachedRawAllocations != null && CachedRawAllocations.Count > 0` | Cache populated |
| B5 | `PerFrameBytes != null && PerFrameBytes.Length > 0` | Computed from allocs |
| B6 | Sub-range active → `FrameStart >= FullFrameStart && FrameEnd <= FullFrameEnd` | Sub-range within full |
| B7 | `m_GraphController != null` | Controller recreated in CreateGUI |

**Implementation:**

```csharp
void ValidateState(string phase)
{
    if (!m_Snapshot.HasData) return;

    var sb = m_SharedSB;
    sb.Clear();
    bool ok = true;

    void Fail(string msg) { ok = false; sb.Append("  FAIL: "); sb.AppendLine(msg); }

    // Phase A invariants (skeleton)
    if (m_Snapshot.GroupsByFullCallstack.Count == 0) Fail("GroupsByFullCallstack empty");
    if (m_Snapshot.GroupsByTopFrame.Count == 0) Fail("GroupsByTopFrame empty");
    if (m_ActiveGroups == null) Fail("m_ActiveGroups null");
    if (m_FrameStore.FullFrameBytes == null) Fail("FullFrameBytes null");
    else if (m_FrameStore.FullFrameBytes.Length !=
             m_FrameStore.FullFrameEnd - m_FrameStore.FullFrameStart + 1)
        Fail("FullFrameBytes.Length mismatch");
    if (m_GraphController == null) Fail("m_GraphController null");

    // Phase B invariants (allocs loaded)
    if (phase == "allocs")
    {
        if (m_Snapshot.RawAllocations.Count != m_Snapshot.TotalCount)
            Fail($"RawAllocations.Count ({m_Snapshot.RawAllocations.Count}) != TotalCount ({m_Snapshot.TotalCount})");
        if (m_ThreadIndexNames.Count != m_Snapshot.SortedThreadNames.Count)
            Fail($"ThreadIndexNames.Count ({m_ThreadIndexNames.Count}) != SortedThreadNames.Count ({m_Snapshot.SortedThreadNames.Count})");
        if (m_FrameStore.CachedRawAllocations == null || m_FrameStore.CachedRawAllocations.Count == 0)
            Fail("CachedRawAllocations empty");
        if (m_Snapshot.PerFrameBytes == null) Fail("PerFrameBytes null");
        if (m_Snapshot.FrameStart < m_FrameStore.FullFrameStart)
            Fail("FrameStart before FullFrameStart");
        if (m_Snapshot.FrameEnd > m_FrameStore.FullFrameEnd)
            Fail("FrameEnd after FullFrameEnd");
    }

    if (ok)
        Debug.Log($"[GCAllocAnalyzer] ValidateState({phase}): ALL PASS");
    else
        Debug.LogError($"[GCAllocAnalyzer] ValidateState({phase}) FAILURES:\n{sb}");
}
```

**Call sites:**
- End of `TryRestoreAfterReload`, before the background thread launch: `ValidateState("skeleton");`
- End of `OnAllocsRestoredFromFile`, after status label update: `ValidateState("allocs");`

**Step 1:** Add `ValidateState` method to `GCAllocAnalyzerWindow.cs`
**Step 2:** Add call at end of `TryRestoreAfterReload`
**Step 3:** Add call at end of `OnAllocsRestoredFromFile`
**Step 4:** Build and verify

---

### Task 4: Manual smoke test with ValidateState

Trigger a domain reload in each scenario. Instead of visually inspecting every element,
check the console for `ValidateState` PASS/FAIL output:

| # | Setup | Expected console output |
|---|-------|------------------------|
| 1 | Full-range analysis | `ValidateState(skeleton): ALL PASS` then `ValidateState(allocs): ALL PASS` |
| 2 | Sub-range drag-select | Same two PASS lines, status bar shows sub-range |
| 3 | Window open, no analysis | No ValidateState output (HasData is false) |
| 4 | Two rapid reloads | No errors, PASS lines after second reload |

Any FAIL line identifies exactly which invariant broke and what the values were.

---

### Task 5: Commit all domain reload fixes

Commit all uncommitted changes:
- Compile error fixes (`SetData` signature, `group.Allocations` removal)
- `m_SnapshotDirty` removed from sub-range rebuilds
- `RestoreState` null buffer guard + `RebuildFrameSelectionBuffer`
- `QueueBackgroundWrite` wiring in `RunAnalysis`
- `m_GroupByCallsite` field on controller
- Sub-range restore in `OnAllocsRestoredFromFile`
- `m_ThreadIndexNames` rebuild
- `ValidateState` invariant checker

```
git add Editor/GCAllocAnalyzerWindow.cs Editor/PerFrameGraphController.cs Editor/GCAllocAnalyzerData.cs
git commit -m "Fix domain reload: sub-range restore, background write, frame selection, invariant checker"
```
