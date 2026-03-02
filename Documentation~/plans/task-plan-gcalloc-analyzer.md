# GC Alloc Analyzer — Verified Task Plan

## Gap Analysis Verification

I reviewed every item in the gap analysis against the actual `GCAllocAnalyzerWindow.cs` (2,209 lines). Here's what I confirmed:

### Confirmed Accurate
- **No Save/Load** — toolbar (`BuildToolbar()` L214–270) only has Pull Data, Analyze, frame range fields, and Open Profiler. No serialization to disk.
- **No compare columns** — `CallsiteGroup` (L2177–2192) has only `TotalBytes`, `Count`, `Percentage`, and display strings. No left/right/diff fields.
- **Single-column Data Summary** — `UpdateDataSummary()` (L1264–1276) writes flat label text. No grid layout.
- **No per-frame graph** — no bar chart, no `perFrameBytes` array. Nothing in the data model or UI.
- **No per-frame stats on groups** — `BuildGrouping()` (L1028–1077) only computes `TotalBytes`, `Count`, `Percentage`, `FormattedAvg`. No median/min/max/mean per frame.
- **No exclude filter** — `ApplyFilters()` (L1105–1139) only checks `m_NameFilter` for inclusion. No exclude field in the UI or filter logic.
- **No column presets** — `BuildMarkerHeaders()` (L502–525) is hardcoded: Bytes, Count, Avg, %, Allocation Site.
- **No right-click context menus** — no `ContextualMenuPopulateEvent` registered anywhere in the file.
- **No CSV export** — no `StreamWriter`, no `SaveFilePanel`, no export logic.
- **Marker Summary is basic** — `UpdateMarkerSummary()` (L1460–1505) shows Total, Count, Avg, % of total. No FirstFrame, no Top-N-by-frame, no min/max frame links.
- **No inline proportional bars** — `MakeMarkerRow()` (L1205–1221) and `BindMarkerRow()` (L1223–1241) are text-only Labels.
- **No copy-to-clipboard** — no reference to `EditorGUIUtility.systemCopyBuffer`.
- **No thread summary section** — thread info only in filter dropdown and status bar.

### One Minor Clarification
- **2.5 (Total/Self)** — the gap analysis correctly marks this as Skip. The code groups by callstack key, and all GC.Alloc samples are inherently "self" allocations. No change needed. ✓
- **2.7 (Remove Marker)** — the gap analysis says "Low-Med complexity." Looking at the code, this would require recalculating `m_AnalyzedTotalBytes` and all `Percentage` fields on every group. More like Medium, but still feasible.

### Verdict: The gap analysis is accurate. No corrections needed.

---

## Task Plan — 12 Tasks, One at a Time

Each task below is self-contained. I've listed what files/methods change, what's new, and a definition of done.

---

### Task 1: Extract `AnalysisSnapshot` (Refactor)

**Goal:** Pull all analysis state out of the window class into a serializable data object so it can be saved/loaded and so Compare mode can hold two snapshots.

**What changes:**
- New class `AnalysisSnapshot` containing:
  - `List<RawAllocation> RawAllocations`
  - `List<string> SortedThreadNames`
  - `long TotalBytes`, `int TotalCount`, `int FrameStart`, `int FrameEnd`, `bool HadCallStacks`
  - `List<CallsiteGroup> GroupsByFullCallstack`, `List<CallsiteGroup> GroupsByTopFrame`
- `RunAnalysis()` returns/populates an `AnalysisSnapshot` instead of writing to `m_RawAllocations`, `m_AnalyzedTotalBytes`, etc. directly
- `BuildGrouping()` takes and populates an `AnalysisSnapshot`
- All downstream methods (`ApplyFilters`, `UpdateDataSummary`, `BuildTopOffenders`, etc.) read from a "current snapshot" reference
- Domain reload (`TryRestoreAfterReload`) works with the snapshot object

**What doesn't change:** UI layout, visible behavior. This is a pure refactor.

**Definition of done:** Tool works identically to before. All serialized fields that survive domain reload still do. The `AnalysisSnapshot` object can be constructed and passed around independently.

---

### Task 2: Save / Load Snapshot to Disk

**Goal:** Users can save an analysis to a `.json` file and reload it later without needing the original profiler data.

**What changes:**
- `BuildToolbar()`: Add **Save** and **Load** buttons after the Analyze button
- Save handler: `JsonUtility.ToJson(snapshot)` → `EditorUtility.SaveFilePanel` → `File.WriteAllText` (use `JsonUtility`, not `EditorJsonUtility` — the snapshot is a plain `[Serializable]` class, not a `UnityEngine.Object`)
- Load handler: `EditorUtility.OpenFilePanel` → `File.ReadAllText` → `JsonUtility.FromJson<AnalysisSnapshot>` → rebuild groupings from loaded snapshot → refresh UI
- `AnalysisSnapshot` must be `[Serializable]` with all nested types also serializable (already the case for `RawAllocation` and `ResolvedFrame`; `CallsiteGroup` needs `[Serializable]`)

**What doesn't change:** Analysis logic, filter logic, UI layout.

**Definition of done:** User can Analyze → Save → close window → reopen → Load → see identical data. File is human-readable JSON.

---

### Task 3: Per-Frame Statistics on `CallsiteGroup`

**Goal:** Compute Median, Mean, Min, Max bytes-per-frame for each callsite group, enabling richer display and future graph overlays.

**What changes:**
- New fields on `CallsiteGroup`:
  ```csharp
  public float MeanBytesPerFrame;
  public long MedianBytesPerFrame;
  public long MinBytesPerFrame;
  public long MaxBytesPerFrame;
  public int MinFrame;   // frame index of min
  public int MaxFrame;   // frame index of max
  public int FirstFrame; // first frame this site allocated
  ```
- New method `ComputePerFrameStats(CallsiteGroup group, int frameStart, int frameEnd)`:
  - Build a `long[]` of per-frame totals (bucket allocations by `FrameIndex`)
  - Sort the array, extract median, min, max, mean
  - Record which frame indices hold min/max
- Called from `BuildGrouping()` after all allocations are assigned to groups
- Also build and store `long[] PerFrameBytes` on the snapshot (total bytes per frame across all groups) — this feeds Task 4

**What doesn't change:** UI display (stats are computed but not yet shown in new columns — that comes in Tasks 4 and 6). Existing columns continue to work.

**Definition of done:** After analysis, every `CallsiteGroup` has accurate per-frame statistics. `AnalysisSnapshot` has a `PerFrameBytes` array. Values can be inspected via debugger or logged.

---

### Task 4: Per-Frame Allocation Bar Graph

**Goal:** A horizontal bar chart at the top of the left panel showing total GC bytes per frame, with spike visualization.

**What changes:**
- New UI section between the filters foldout and the marker list header
- A custom `VisualElement` (or a container of thin vertical bars) drawn from `PerFrameBytes[]`
- Bar height proportional to max allocation in the range
- Single bar color matching Unity Profile Analyzer's style — relative heights communicate spikes
- Click on a bar → select that frame in the Profiler (`SelectInCpuModule` with the frame index)
- When a `CallsiteGroup` is selected in the marker list, overlay a second color showing that group's per-frame contribution
- Reasonable height, collapsible via a foldout or toggle

**What doesn't change:** Right panel, toolbar, filter logic.

**Definition of done:** After analysis, a bar chart appears showing per-frame GC allocation. Spikes are visually obvious. Clicking a bar jumps to that frame. Selecting a marker shows its overlay.

---

### Task 5: Exclude Names Filter + Context Menus + CSV Export

**Goal:** Three small-but-impactful features bundled because they're each low complexity.

#### 5a: Exclude Names Filter
**What changes:**
- New `TextField m_ExcludeFilter` in the Filters foldout (second row, next to Name filter)
- In `ApplyFilters()`, after the name inclusion check, add exclusion check:
  ```csharp
  if (excludeFilter.Length > 0 &&
      g.DisplayName.IndexOf(excludeFilter, StringComparison.OrdinalIgnoreCase) >= 0)
      continue;
  ```

#### 5b: Right-Click Context Menus
**What changes:**
- Register `ContextualMenuPopulateEvent` on marker list rows in `MakeMarkerRow()`
- Menu items: "Copy Name", "Add to Name Filter", "Add to Exclude Filter", "Open Source File"
- Register on call stack frame rows in `BuildCallStackDisplay()`: "Open Source File", "Copy Method Name"
- Register on individual allocation rows in `MakeAllocRow()`: "Jump to Frame in Profiler", "Copy Details"

#### 5c: CSV Export
**What changes:**
- New **Export ▾** button in `BuildToolbar()` that opens a `GenericMenu` with options:
  - "Marker Table CSV" — exports all filtered groups with all computed statistics
  - "Individual Allocations CSV" — every raw allocation
- Uses `EditorUtility.SaveFilePanel` + `StreamWriter`

**Definition of done:** Exclude filter hides matching sites. Right-click on marker rows/callstack frames/alloc rows shows appropriate context menus. CSV export produces valid, openable CSV files.

---

### Task 6: Per-Frame Stats Columns

**Goal:** Surface per-frame statistics as sortable columns in the marker table, matching Profile Analyzer's style.

**What changes:**
- Add sortable columns to the marker MultiColumnListView: **Median**, **Mean**, **Min**, **Max**, **Range**, **First**
- Per-frame stats computed during grouping (from Task 3 data), displayed as pre-formatted strings
- Columns sortable like existing columns (Bytes, Count, Avg, %, Name)
- Column header tooltips for clarity

**What doesn't change:** Right panel (call stack, individual allocations list, marker stats label). No clickable frame navigation or per-marker detail enrichment.

**Definition of done:** Marker table has 6 new sortable columns showing per-frame statistics. Users can sort all markers by any stat to find spikiest, most variable, etc.

---

### Task 7: Graph Interactivity & Custom Rendering

**Goal:** Replace the UIElements-per-bar graph with a custom GL-rendered graph (referencing Profile Analyzer's `Draw2D`/`FrameTimeGraph`) and add full interactivity: drag-selection, keyboard navigation, sorted view, and grid lines. This establishes the graph infrastructure that Compare mode's paired graphs will reuse.

**Reference:** Unity Profile Analyzer's `FrameTimeGraph.cs` (~2300 lines) and `Draw2D.cs` (~210 lines) in `Library/PackageCache/com.unity.performance.profile-analyzer`. Follow the same patterns but render within UIElements (via `generateVisualContent` / `MeshGenerationContext`) rather than IMGUI's `GL.Begin`/`GL.End`.

**What changes:**

#### 7a: Custom GL Rendering Engine
- New `Draw2D` utility class adapted from Profile Analyzer — draws filled boxes, lines, outlined boxes using `Painter2D` or `MeshGenerationContext` within a UIElements `VisualElement.generateVisualContent` callback
- Custom shader with clip rect support (adapt `ProfileAnalyzerShader.shader` for UIElements context)
- Replace per-bar `VisualElement` creation with a single custom-drawn element — eliminates the VisualElement-per-bucket scaling concern noted in `future-improvements.md`

#### 7b: Bar Rendering with Selection Highlighting
- `RegenerateBars()` method matching Profile Analyzer's approach: one bar per pixel (or per data point if fewer than pixels), storing `BarData` structs with position, height, min/max, data offset ranges
- Two-pass rendering: background fill → selected region background → bars (selected color vs normal color) → overlay
- Selected bar/frame highlighting with distinct color when a frame is clicked
- Overflow indicators for bars exceeding Y-axis range

#### 7c: Drag-to-Select Frame Range
- Mouse input handling: click & drag to create a selection range on the graph
- Shift+click inside selection to move the entire selection
- Selection state: `None`, `Dragging`, `DragComplete`
- Selection callback notifies main window — could update toolbar frame range fields or provide a "Re-analyze Selection" action
- Visual: selected region gets a distinct background color, selected bars use `m_ColorBarSelected`

#### 7d: Keyboard Navigation
- Arrow keys: move selection left/right (shift for 10-frame steps)
- `+` / `-`: grow/shrink selection symmetrically
- `<` / `>`: grow/shrink left/right edge independently

#### 7e: Multiple Grid Lines with Labels
- Horizontal grid lines at meaningful byte thresholds (auto-computed based on Y-axis range)
- Labels on grid lines (e.g., "1 MB", "512 KB")
- Frame index labels on X-axis with smart positioning to avoid overlap

#### 7f: Order-by-Magnitude Sorted View
- Toggle to reorder bars by allocation size (descending) instead of frame order
- Reveals distribution shape: one spike vs consistently high allocations
- Matching Profile Analyzer's `showOrderedByFrameDuration` toggle

#### 7g: Context Menu
- Right-click menu: "Select All", "Clear Selection", "Select Frame with Max GC", "Select Frame with Min GC", "Zoom to Selection", "Zoom All"

#### 7h: Marker Overlay
- When a `CallsiteGroup` is selected, draw overlay bars in a distinct color showing that group's per-frame contribution (ported from current overlay logic into the new rendering path)

**What doesn't change:** Data model, toolbar, right panel, filter logic. The graph still reads from `AnalysisSnapshot.PerFrameBytes` and group allocation data.

**Definition of done:** Graph renders via custom drawing, supports drag-select with keyboard navigation, shows grid lines, has sorted view toggle and context menu. Clicking a bar still jumps to that frame in the Profiler. Marker overlay still works. No VisualElement-per-bar scaling concern remains.

---

### Task 8: Mode Tabs — Single / Compare

**Goal:** Add a tab bar at the top of the window to switch between Single and Compare modes.

**What changes:**
- New enum `AnalysisMode { Single, Compare }`
- Tab bar UI at the very top of `CreateGUI()` (above toolbar): two tabs, styled like Profile Analyzer
- `m_CurrentMode` field, serialized to survive reload
- When switching modes, show/hide mode-specific UI sections (toolbar rows, left panel columns, right panel layout)
- Compare mode UI is initially empty/placeholder — subsequent tasks fill it in

**What doesn't change:** All Single mode functionality remains identical. Compare mode is a shell.

**Definition of done:** Two tabs appear. Clicking "Single" shows the current tool. Clicking "Compare" shows a placeholder. Mode survives domain reload.

---

### Task 9: Compare Data Model

**Goal:** The data structures for holding two snapshots and computing deltas between matched groups.

**What changes:**
- New class `ComparedGroup`:
  ```csharp
  class ComparedGroup
  {
      public CallsiteGroup Left;   // null if only in Right
      public CallsiteGroup Right;  // null if only in Left
      public long DeltaBytes;
      public int DeltaCount;
      public float DeltaPercent;   // DeltaBytes / Left.TotalBytes * 100
  }
  ```
- New fields on the window: `AnalysisSnapshot m_LeftSnapshot`, `AnalysisSnapshot m_RightSnapshot`
- New method `BuildComparison()` that matches groups by `Key` across Left and Right snapshots, produces `List<ComparedGroup>`
- Matching strategy: exact key match (same callsite). Unmatched groups appear as left-only or right-only.

**What doesn't change:** Single mode. UI (this is data-only).

**Definition of done:** Given two snapshots, `BuildComparison()` produces a correct list of `ComparedGroup` objects with accurate deltas. Unit-testable logic.

---

### Task 10: Compare Toolbar (Dual Pull/Load/Save Rows)

**Goal:** In Compare mode, the toolbar shows two independent rows — one for Left, one for Right — each with Pull Data, Load, Save, frame range.

**What changes:**
- `BuildToolbar()` becomes mode-aware: calls `BuildSingleToolbar()` or `BuildCompareToolbar()` based on `m_CurrentMode`
- `BuildCompareToolbar()` creates two rows:
  - Row 1 (Left): `[Pull Data] [Load] [Save] | Frames: [start]–[end] | (N frames)`
  - Row 2 (Right): same layout, independent fields
- Each Pull/Analyze triggers analysis into the respective snapshot (`m_LeftSnapshot` or `m_RightSnapshot`)
- After both snapshots are populated, auto-run `BuildComparison()` and refresh

**Definition of done:** In Compare mode, two toolbar rows appear. Each can independently pull/load data. Status bar reflects which snapshots are populated.

---

### Task 11: Compare Left Panel (Paired Marker List with Delta Columns)

**Goal:** The marker list in Compare mode shows Left Bytes, Right Bytes, Δ Bytes, Δ%, Left Count, Right Count, Δ Count with colored bars.

**What changes:**
- New `BuildCompareMarkerHeaders()` with columns: Name, Left Bytes, Right Bytes, Δ Bytes, |Δ Bytes|, Δ%, Left Count, Right Count, Δ Count
- New `MakeCompareMarkerRow()` / `BindCompareMarkerRow()` that renders `ComparedGroup` items
- Inline colored bars: blue bar for Left, orange bar for Right, proportional to max in list
- Sorting on any delta column
- Color coding: green for improvements (negative Δ), red for regressions (positive Δ)
- Filter applies to both Left and Right display names

**Definition of done:** Compare mode marker list shows all paired groups with delta columns, visual bars, and color coding. Sorting works on all columns.

---

### Task 12: Compare Per-Frame Graphs (Paired Left/Right)

**Goal:** In Compare mode, show two paired per-frame graphs (Left and Right) that reuse the custom GL graph infrastructure from Task 7, with synced selection and visual comparison of allocation patterns.

**Reference:** Profile Analyzer creates `m_LeftFrameTimeGraph` and `m_RightFrameTimeGraph` as separate `FrameTimeGraph` instances sharing a `Draw2D` renderer, with a pairing system that syncs selection between them.

**What changes:**
- Two `PerFrameGraphController` instances in Compare mode — one for `m_LeftSnapshot`, one for `m_RightSnapshot`
- **Pairing system:** selection on one graph syncs to the other (drag-select on Left selects the same frame range on Right, and vice versa). Matching Profile Analyzer's `SetPairing()` / `PairTo()` pattern
- **Layout:** stacked vertically with "Left" / "Right" labels, or side-by-side if window is wide enough
- **Overlay:** selecting a `ComparedGroup` in the compare marker list shows that group's overlay on both graphs simultaneously, making it easy to see how a specific call site's allocation pattern changed between runs
- **Shared Y-axis range:** option to lock both graphs to the same Y-axis max so bar heights are directly comparable, or auto-scale each independently (toggle in context menu)
- All Task 7 interactivity available on both graphs: drag-select, keyboard nav, grid lines, sorted view, context menu

**What doesn't change:** Single mode graph. Compare data model. Compare toolbar.

**Definition of done:** Compare mode shows two paired graphs. Selecting a frame range on one syncs to the other. Marker overlay works on both. Shared Y-axis toggle works. All interactive features from Task 7 function on both graphs.

---

### Task 13: Compare Right Panel (L/R/Diff Summary + Regressions/Improvements)

**Goal:** The right panel in Compare mode shows a three-column data summary grid and Top Regressions / Top Improvements lists.

**What changes:**
- `UpdateDataSummary()` becomes mode-aware
- Compare summary uses a table grid:
  ```
                      Left         Right        Diff
  Frame Count:        1000         585          -415
  Total GC:           48.2 KB      31.7 KB      -16.5 KB  (-34.2%)
  Total Allocs:       2,340        1,891        -449      (-19.2%)
  Unique Sites:       156          142          -14
  ```
- New **Top Regressions** foldout: top 10 `ComparedGroup` by positive `DeltaBytes`
- New **Top Improvements** foldout: top 10 by negative `DeltaBytes`
- Selecting a `ComparedGroup` shows side-by-side marker detail (Left stats / Right stats / Diff)

**Definition of done:** Compare mode right panel shows L/R/Diff grid, regressions, improvements. Selecting a compared marker shows side-by-side detail.

---

### Task 14: Polish (Bars, Copy, Thread Summary)

**Goal:** Final polish pass bringing in the remaining P3 items. Graph interactivity (3.4, 3.5) moved to Task 7.

**What changes:**
- **3.1 Inline proportional bars** in Single mode marker list (colored `VisualElement` behind text, width ∝ value / max)
- **3.7 Copy to Clipboard** — "Copy Summary" button in Marker Summary → `EditorGUIUtility.systemCopyBuffer`
- **3.3 Thread Summary** — new foldout in right panel showing per-thread allocation totals and counts

**Definition of done:** Visual bars in marker list. Copy button works. Thread summary section shows per-thread breakdown.

---

## Recommended Execution Order

```
Task 1  → Task 2  → Task 3  → Task 4  → Task 5  → Task 6  → Task 7
  (refactor)  (save/load) (stats)   (graph)   (filter+   (stats     (graph
                                               menus+csv)  columns)   interactivity)

Task 8  → Task 9  → Task 10 → Task 11 → Task 12 → Task 13 → Task 14
  (tabs)    (compare   (compare   (compare   (compare   (compare   (polish)
             model)     toolbar)   left)      graphs)    right)
```

Tasks 1–6 strengthen Single mode. Task 7 upgrades the graph to custom GL rendering with full interactivity. Tasks 8–13 build Compare mode on that foundation (Task 12 reuses the graph infrastructure for paired left/right graphs). Task 14 is a polish pass across both modes.

**Start with Task 1** — it's a pure refactor with no UI changes, making it safe and foundational for everything that follows.
