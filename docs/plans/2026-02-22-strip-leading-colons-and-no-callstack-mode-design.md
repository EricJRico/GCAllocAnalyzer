# Design: Strip Leading :: and No-Callstack Mode

## Problem

1. When a method has no namespace, the IL2CPP format `Assembly!::Class.Method` displays as `::Class.Method` after assembly stripping — the leading `::` is confusing.
2. When call stacks aren't enabled in the Profiler, the analyzer still collects allocation data (size, parent method, hierarchy path, thread, frame) but presents it poorly — grouping everything as `"[no callstack]"` and showing only a "please enable" message in the details panel.

## Design

### Issue 1: Strip Leading `::`

**Change `StripAssembly` in both files** to additionally strip a leading `::` from the result.

Files:
- `GCAllocAnalyzerWindow.cs` — `StripAssembly()` (line ~1869)
- `GCAllocBreakdownModule.cs` — `StripAssembly()` (line ~647)

Logic: after removing the assembly prefix (before `!`), if the remainder starts with `::`, skip those 2 characters.

Examples:
- `Assembly!::ClassName.Method` → `ClassName.Method` (fixed)
- `Assembly!System.Collections::List.Add` → `System.Collections::List.Add` (unchanged)
- `ClassName.Method` (no `!`, no `::` prefix) → `ClassName.Method` (unchanged)

This single change flows through all display paths: `FormatTopFrame`, `FormatStackFrameDisplay`, keys, tooltips.

### Issue 2: No-Callstack Mode

#### A. Display name (main window)

In `RunAnalysis`, change the no-callstack display name from:
```csharp
string.Concat(parentMethod, "  [no callstack]")
```
to just:
```csharp
parentMethod
```

The status bar already warns when call stacks are not detected (`"⚠ Call Stacks: NOT detected"`).

#### B. Call stack panel fallback (main window)

In `BuildCallStackDisplay`, when `ResolvedCallStack` is null/empty:
- Parse `HierarchyPath` from the first allocation in the group (split on ` > `)
- Display each segment as a read-only text row, styled like caller frames (gray)
- Show the last segment (parent method) styled like the top frame (yellow)
- Add a subtle note at the bottom: "Hierarchy path shown — enable Call Stacks for full detail"

#### C. Module extraction (profiler module)

In `GCAllocBreakdownModule.ExtractFrame`:
- Add a depth stack (list of `DepthEntry`) to track the profiler hierarchy during sample iteration, mirroring the main window's approach
- For no-callstack allocations, use the parent method name as the display name instead of `"[no callstack — threadName]"`
- Group key becomes `"nostack|" + parentMethod` instead of `"nostack|" + threadName`

#### D. Module display

No changes needed to `BindRow` — it already displays `g.DisplayName`, which will now be the parent method name.

## Scope

- 2 files modified
- No new files
- No new UI controls
- No breaking changes to serialized data (keys change format but are not persisted across sessions)
