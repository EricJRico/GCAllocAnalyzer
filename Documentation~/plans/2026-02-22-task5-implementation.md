# Task 5: Exclude Filter + Context Menus + CSV Export — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add three small features to GCAllocAnalyzerWindow: an exclude-names filter, right-click context menus on marker/callstack/alloc rows, and CSV export for marker table and individual allocations.

**Architecture:** All changes are in `GCAllocAnalyzerWindow.cs`. The exclude filter adds a text field and two lines to `ApplyFilters()`. Context menus use UIElements' `ContextualMenuPopulateEvent` with `userData` to avoid closures in virtualized lists. CSV export adds a toolbar button opening a `GenericMenu` with two export options using `StreamWriter`.

**Tech Stack:** Unity 6000.3.6f1, UIElements, C# 9.0

---

### Task 1: Add Exclude Names Filter

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Step 1: Add the UI field declaration**

At line 57, after `TextField m_NameFilter;`, add the exclude filter field:

```csharp
TextField m_ExcludeFilter;
```

At line 148, after `string m_LastNameFilter = "";`, add:

```csharp
string m_LastExcludeFilter = "";
```

**Step 2: Add the exclude TextField to the filters foldout**

In `BuildLeftPanel()`, after `m_NameFilter` is added to `filterRow1` (after line 477), add:

```csharp
m_ExcludeFilter = new TextField("Exclude:")
{
    style = { minWidth = 150, flexGrow = 1, marginRight = 12 }
};
m_ExcludeFilter.RegisterValueChangedCallback(OnExcludeFilterChanged);
filterRow1.Add(m_ExcludeFilter);
```

**Step 3: Add the callback**

After `OnNameFilterChanged` (line 896), add:

```csharp
void OnExcludeFilterChanged(ChangeEvent<string> evt) => ApplyFilters();
```

**Step 4: Add exclusion logic to `ApplyFilters()`**

In `ApplyFilters()`, after the existing `nameFilter` variable (line 1761), add:

```csharp
string excludeFilter = m_ExcludeFilter != null ? m_ExcludeFilter.value : "";
```

After the name inclusion `continue` (after line 1772), add:

```csharp
// Exclude filter
if (excludeFilter.Length > 0 &&
    g.DisplayName.IndexOf(excludeFilter, StringComparison.OrdinalIgnoreCase) >= 0)
    continue;
```

**Step 5: Persist exclude filter across domain reload**

In `TryRestoreAfterReload()`, the `m_NameFilter.value` is restored via `[SerializeField]` on the TextField. UIElements TextFields auto-serialize their value when declared as serialized fields. Since `m_ExcludeFilter` is an instance field on the EditorWindow, it will survive domain reload like `m_NameFilter` does.

No additional restore logic needed — UIElements handles it.

**Step 6: Commit**

```
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: add exclude names filter to GC Alloc Analyzer"
```

---

### Task 2: Add Context Menu to Marker Rows

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Step 1: Register context menu event in `MakeMarkerRow()`**

`MakeMarkerRow()` is `static`, so it cannot register an instance method directly. Instead, change it from `static` to instance and register the callback. At line 1857:

Change `static VisualElement MakeMarkerRow()` to `VisualElement MakeMarkerRow()`.

Before `return row;` (line 1872), add:

```csharp
row.AddManipulator(new ContextualMenuManipulator(OnMarkerRowContextMenu));
```

**Step 2: Set `userData` in `BindMarkerRow()`**

At line 1878 (after getting `var g`), add:

```csharp
el.userData = g;
```

**Step 3: Implement the context menu handler**

After `BindMarkerRow()` (after line 1893), add the handler method:

```csharp
void OnMarkerRowContextMenu(ContextualMenuPopulateEvent evt)
{
    var group = (evt.currentTarget as VisualElement)?.userData as CallsiteGroup;
    if (group == null) return;

    evt.menu.AppendAction("Copy Name", _ =>
        EditorGUIUtility.systemCopyBuffer = group.DisplayName);

    evt.menu.AppendAction("Add to Name Filter", _ =>
    {
        if (m_NameFilter != null) m_NameFilter.value = group.DisplayName;
    });

    evt.menu.AppendAction("Add to Exclude Filter", _ =>
    {
        if (m_ExcludeFilter != null) m_ExcludeFilter.value = group.DisplayName;
    });

    // Open Source File — disabled when no source info
    bool canOpen = group.ResolvedCallStack != null &&
                   group.ResolvedCallStack.Count > 0 &&
                   CanOpenScript(group.ResolvedCallStack[0]);
    evt.menu.AppendAction("Open Source File",
        _ => { if (group.ResolvedCallStack.Count > 0) OpenScript(group.ResolvedCallStack[0]); },
        canOpen ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
}
```

**Step 4: Update `m_MarkerListView.makeItem` assignment**

At line 529, `m_MarkerListView.makeItem = MakeMarkerRow;` — since `MakeMarkerRow` is now an instance method, this already works (instance method group converts to `Func<VisualElement>`). No change needed.

**Step 5: Commit**

```
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: add right-click context menu to marker rows"
```

---

### Task 3: Add Context Menu to Call Stack Frame Rows

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Step 1: Add context menu to frame rows in `BuildCallStackDisplay()`**

In the loop at line 2207, after `frameRow.Add(frameLbl);` (line 2234) and before the "Open script button" block, add the context menu. Since `BuildCallStackDisplay` rebuilds the entire container on every selection (not a hot path), closures are acceptable here (consistent with existing pattern at line 2239–2241):

```csharp
// Context menu — closures acceptable (not a hot path, rebuilt per selection)
var capturedFrameForMenu = frame;
frameRow.AddManipulator(new ContextualMenuManipulator(menuEvt =>
{
    menuEvt.menu.AppendAction("Copy Method Name", _ =>
        EditorGUIUtility.systemCopyBuffer = capturedFrameForMenu.RawMethodName);

    bool canOpenSource = CanOpenScript(capturedFrameForMenu);
    menuEvt.menu.AppendAction("Open Source File",
        _ => OpenScript(capturedFrameForMenu),
        canOpenSource ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
}));
```

Insert this right after `frameRow.Add(frameLbl);` (line 2234), before the existing `if (CanOpenScript(frame))` block (line 2237).

**Step 2: Commit**

```
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: add right-click context menu to call stack frame rows"
```

---

### Task 4: Add Context Menu to Allocation Rows

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Step 1: Change `MakeAllocRow()` from static to instance and register context menu**

At line 2284, change `static VisualElement MakeAllocRow()` to `VisualElement MakeAllocRow()`.

Before `return row;` (line 2296), add:

```csharp
row.AddManipulator(new ContextualMenuManipulator(OnAllocRowContextMenu));
```

**Step 2: Set `userData` in `BindAllocRow()`**

At line 2302 (after getting `var a`), add:

```csharp
el.userData = a;
```

**Step 3: Implement the context menu handler**

After `BindAllocRow()` (after line 2311), add:

```csharp
void OnAllocRowContextMenu(ContextualMenuPopulateEvent evt)
{
    var alloc = (evt.currentTarget as VisualElement)?.userData as RawAllocation;
    if (alloc == null) return;

    evt.menu.AppendAction("Jump to Frame in Profiler", _ =>
    {
        if (!m_IsLoadedSnapshot) SelectInCpuModule(alloc);
    },
    m_IsLoadedSnapshot ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);

    var sb = new StringBuilder(128);
    sb.Append(alloc.FormattedBytes);
    sb.Append(" | Frame "); sb.Append(alloc.FormattedFrame);
    sb.Append(" | "); sb.Append(alloc.ThreadDisplayName);
    sb.Append(" | "); sb.Append(alloc.HierarchyPath);
    string details = sb.ToString();

    evt.menu.AppendAction("Copy Details", _ =>
        EditorGUIUtility.systemCopyBuffer = details);
}
```

**Step 4: Commit**

```
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: add right-click context menu to allocation rows"
```

---

### Task 5: Add CSV Export Button and GenericMenu

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Step 1: Add Export button field declaration**

At line 64, after `Button m_LoadBtn;`, add:

```csharp
Button m_ExportBtn;
```

**Step 2: Add the Export button in `BuildToolbar()`**

After the `m_LoadBtn` block (after line 269), add:

```csharp
m_ExportBtn = new Button(ShowExportMenu) { text = "Export \u25be", style = { marginRight = 12 } };
m_ExportBtn.SetEnabled(false);
bar.Add(m_ExportBtn);
```

Remove the `style = { marginRight = 12 }` from `m_LoadBtn` (line 268) and change it to `marginRight = 2` so spacing flows correctly:

```csharp
m_LoadBtn = new Button(OnLoadSnapshot) { text = "Load", style = { marginRight = 2 } };
```

Also remove the `bar.Add(MakeSeparator());` line (line 271) — the Export button replaces the separator visually. Actually, keep the separator after the Export button instead. So the order becomes: Load (marginRight=2) → Export (marginRight=12) → Separator → Frames.

**Step 3: Enable/disable Export button alongside Save button**

Wherever `m_SaveBtn.SetEnabled(true)` is called, also enable `m_ExportBtn`. Search for `m_SaveBtn.SetEnabled` and add `m_ExportBtn.SetEnabled` alongside each call. There should be calls in `RunAnalysis`/post-analysis and in `OnLoadSnapshot`.

**Step 4: Implement `ShowExportMenu()`**

After `ShowThreadFilterMenu()` section (after line 964), add:

```csharp
void ShowExportMenu()
{
    var menu = new GenericMenu();
    menu.AddItem(new GUIContent("Marker Table CSV"), false, ExportMarkerTableCSV);
    menu.AddItem(new GUIContent("Individual Allocations CSV"), false, ExportAllocationsCSV);
    menu.ShowAsContext();
}
```

**Step 5: Commit**

```
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: add Export dropdown button to toolbar"
```

---

### Task 6: Implement Marker Table CSV Export

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Step 1: Implement `ExportMarkerTableCSV()`**

Add after `ShowExportMenu()`:

```csharp
void ExportMarkerTableCSV()
{
    string path = EditorUtility.SaveFilePanel(
        "Export Marker Table CSV", "", "marker-table.csv", "csv");
    if (string.IsNullOrEmpty(path)) return;

    try
    {
        using (var writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            // Header
            writer.WriteLine("Name,Bytes,Count,Avg,Percentage,Median/Frame,Mean/Frame,Min/Frame,Max/Frame,MinFrame,MaxFrame,FirstFrame");

            // Rows — use filtered groups (what user sees)
            for (int i = 0; i < m_FilteredGroups.Count; i++)
            {
                var g = m_FilteredGroups[i];
                writer.Write(EscapeCsvField(g.DisplayName));
                writer.Write(','); writer.Write(g.TotalBytes);
                writer.Write(','); writer.Write(g.Count);
                writer.Write(','); writer.Write(g.TotalBytes / Math.Max(1, g.Count));
                writer.Write(','); writer.Write(g.Percentage.ToString("F2"));
                writer.Write(','); writer.Write(g.MedianBytesPerFrame);
                writer.Write(','); writer.Write(g.MeanBytesPerFrame.ToString("F1"));
                writer.Write(','); writer.Write(g.MinBytesPerFrame);
                writer.Write(','); writer.Write(g.MaxBytesPerFrame);
                writer.Write(','); writer.Write(g.MinFrame);
                writer.Write(','); writer.Write(g.MaxFrame);
                writer.Write(','); writer.Write(g.FirstFrame);
                writer.WriteLine();
            }
        }

        Debug.Log(string.Concat("[GC Alloc Analyzer] Exported ", m_FilteredGroups.Count.ToString(),
            " markers to ", path));
    }
    catch (Exception e)
    {
        Debug.LogError(string.Concat("[GC Alloc Analyzer] CSV export failed: ", e.Message));
    }
}
```

**Step 2: Implement `EscapeCsvField()` helper**

Add the CSV field escaping helper (handles commas, quotes, newlines):

```csharp
static string EscapeCsvField(string field)
{
    if (field == null) return "";
    if (field.IndexOf(',') < 0 && field.IndexOf('"') < 0 &&
        field.IndexOf('\n') < 0 && field.IndexOf('\r') < 0)
        return field;
    return string.Concat("\"", field.Replace("\"", "\"\""), "\"");
}
```

**Step 3: Commit**

```
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: implement marker table CSV export"
```

---

### Task 7: Implement Individual Allocations CSV Export

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Step 1: Implement `ExportAllocationsCSV()`**

Add after `ExportMarkerTableCSV()`:

```csharp
void ExportAllocationsCSV()
{
    string path = EditorUtility.SaveFilePanel(
        "Export Individual Allocations CSV", "", "allocations.csv", "csv");
    if (string.IsNullOrEmpty(path)) return;

    try
    {
        var allocs = m_Snapshot.RawAllocations;
        using (var writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            // Header
            writer.WriteLine("Bytes,Frame,Thread,ParentMethod,HierarchyPath,CallStack");

            // Rows
            var sb = new StringBuilder(256);
            for (int i = 0; i < allocs.Count; i++)
            {
                var a = allocs[i];
                writer.Write(a.Bytes);
                writer.Write(','); writer.Write(a.FrameIndex);
                writer.Write(','); writer.Write(EscapeCsvField(a.ThreadDisplayName));
                writer.Write(','); writer.Write(EscapeCsvField(a.ParentMethod));
                writer.Write(','); writer.Write(EscapeCsvField(a.HierarchyPath));
                writer.Write(',');

                // Call stack — frames joined by " > "
                if (a.ResolvedCallStack != null && a.ResolvedCallStack.Count > 0)
                {
                    sb.Clear();
                    for (int f = 0; f < a.ResolvedCallStack.Count; f++)
                    {
                        if (f > 0) sb.Append(" > ");
                        sb.Append(a.ResolvedCallStack[f].RawMethodName);
                    }
                    writer.Write(EscapeCsvField(sb.ToString()));
                }

                writer.WriteLine();
            }
        }

        Debug.Log(string.Concat("[GC Alloc Analyzer] Exported ", allocs.Count.ToString(),
            " allocations to ", path));
    }
    catch (Exception e)
    {
        Debug.LogError(string.Concat("[GC Alloc Analyzer] CSV export failed: ", e.Message));
    }
}
```

**Step 2: Commit**

```
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: implement individual allocations CSV export"
```

---

### Task 8: Manual Verification

**Step 1: Verify in Unity**

1. Open Unity, enter Play Mode, let test rig run 100+ frames
2. Open Profiler (`Ctrl+7`), enable `Call Stacks → GC.Alloc`
3. Open `Window > Analysis > GC Alloc Analyzer`
4. Click Analyze

**Step 2: Test Exclude Filter**

1. Type a common method name in the Name filter — verify it includes only matches
2. Type the same name in the Exclude filter — verify those rows disappear
3. Type in both filters simultaneously — verify inclusion happens first, then exclusion
4. Clear both — verify all rows return

**Step 3: Test Context Menus**

1. Right-click a marker row — verify menu shows "Copy Name", "Add to Name Filter", "Add to Exclude Filter", "Open Source File"
2. Click "Copy Name" — paste elsewhere to verify
3. Click "Add to Name Filter" — verify the name filter field updates and list filters
4. Click "Add to Exclude Filter" — verify the exclude filter field updates and list filters
5. Click "Open Source File" — verify IDE opens the file (or verify it's disabled when no source)
6. Select a marker with a call stack — right-click a call stack frame row — verify "Copy Method Name" and "Open Source File"
7. Right-click an individual allocation row — verify "Jump to Frame in Profiler" and "Copy Details"

**Step 4: Test CSV Export**

1. Click "Export ▾" — verify dropdown shows two options
2. Click "Marker Table CSV" — save to desktop — open in Excel/text editor — verify header and data
3. Click "Individual Allocations CSV" — save — verify header and data including call stack column
4. Verify commas in method names are properly escaped in the CSV

**Step 5: Test Domain Reload**

1. Set up filters (name + exclude), then trigger a script recompile
2. Verify both filter values persist after domain reload
3. Verify Export button remains enabled if data was loaded

**Step 6: Final commit**

```
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat(task5): exclude filter, context menus, CSV export"
```
