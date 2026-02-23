# Task 5 Design: Exclude Filter + Context Menus + CSV Export

## 5a: Exclude Names Filter

Add `TextField m_ExcludeFilter` to `filterRow1` in the Filters foldout, after the existing name filter. Same sizing/styling as `m_NameFilter`.

In `ApplyFilters()`, after the name inclusion check passes, add exclusion:

```csharp
if (excludeFilter.Length > 0 &&
    g.DisplayName.IndexOf(excludeFilter, StringComparison.OrdinalIgnoreCase) >= 0)
    continue;
```

New callback `OnExcludeFilterChanged` mirrors `OnNameFilterChanged`. New serialized field `m_LastExcludeFilter` for domain reload persistence.

## 5b: Right-Click Context Menus

Use `ContextualMenuPopulateEvent` from UIElements. Three locations:

### Marker rows (`MakeMarkerRow` / `BindMarkerRow`)
- Register callback in `MakeMarkerRow()`
- Set `el.userData = group` in `BindMarkerRow()`
- Items: "Copy Name", "Add to Name Filter", "Add to Exclude Filter", "Open Source File" (disabled when no source info)

### Call stack frame rows (`BuildCallStackDisplay`)
- Register per-row; closures acceptable here (not a hot path, already acknowledged in code)
- Items: "Open Source File", "Copy Method Name"

### Allocation rows (`MakeAllocRow` / `BindAllocRow`)
- Register callback in `MakeAllocRow()`
- Set `el.userData = allocation` in `BindAllocRow()`
- Items: "Jump to Frame in Profiler", "Copy Details"

## 5c: CSV Export

Add "Export" button in toolbar after Load button. Opens a `GenericMenu` (same pattern as `ShowThreadFilterMenu`).

### Marker Table CSV
Columns: Name, Bytes, Count, Avg, %, Median/Frame, Mean/Frame, Min/Frame, Max/Frame, MinFrame, MaxFrame, FirstFrame

### Individual Allocations CSV
Columns: Bytes, Frame, Thread, ParentMethod, HierarchyPath, CallStack (frames joined by ` > `)

Both use `EditorUtility.SaveFilePanel` + `StreamWriter`. Local `StringBuilder` for building lines. CSV values containing commas or quotes are properly escaped.
