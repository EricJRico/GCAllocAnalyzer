# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

GCAllocAnalyzer is a Unity Editor tool (UPM package) for profiling and analyzing garbage collection (GC) allocations. It integrates with Unity's Profiler as both a standalone Editor Window (multi-frame analysis) and a Profiler Module (per-frame analysis). A runtime helper component enables capturing profiler data on devices.

**Package Name**: `com.ericjrico.gcalloc-analyzer`
**Unity Version**: 6000.0+
**Language**: C# 9.0 / .NET 4.7.1

## Package Structure

This repo is a UPM package (not a Unity project). Install via Git URL in Package Manager:
```
https://github.com/EricJRico/GCAllocAnalyzer.git
```

Assembly definitions control compilation:

- `Runtime/GCAllocBreakdown.asmdef` — Runtime assembly (contains `GCAllocCaptureHelper`)
- `Editor/GCAllocBreakdown.Editor.asmdef` — Editor-only assembly (references runtime)
- `Samples~/GCAllocTests/GCAllocTests.asmdef` — Sample test generators (imported via Package Manager)

For local development, create a separate Unity project and reference this package via local path in `Packages/manifest.json`:
```json
"com.ericjrico.gcalloc-analyzer": "file:../GCAllocAnalyzer"
```

## Testing

No automated test suite. Testing is manual via scene-based allocation generators:

1. Import the "GC Alloc Test Rig" sample from Package Manager
2. In Unity: `Tools > GC Alloc Test > Create Test Rig` (adds all test components)
3. Enter Play Mode, let it run 100+ frames
4. Open Profiler (`Ctrl+7`), enable `Call Stacks → GC.Alloc` in toolbar
5. Open analyzer: `Window > Analysis > GC Alloc Analyzer`
6. Set frame range and click Analyze

## Architecture

### Core Source Files

**`Editor/GCAllocAnalyzerWindow.cs`** (~2200 lines)
Main EditorWindow for multi-frame analysis. Extracts GC.Alloc events from profiler data across a frame range, groups by full call stack or top frame, provides sorting/filtering/top-offenders views. Uses UIElements with virtualized ListViews.

**`Editor/GCAllocBreakdownModule.cs`** (~750 lines)
ProfilerModule + ProfilerModuleViewController for per-frame breakdown inside Unity's Profiler window. Uses an LRU cache (512 frames) for instant frame switching.

**`Editor/PerFrameGraphController.cs`** (~1576 lines)
Per-frame graph with zoom/pan (WASD + mouse wheel), overview strip, drag-select sub-range analysis.

**`Editor/GraphElement.cs`** (~673 lines)
Custom Painter2D rendering for the per-frame bar graph.

**`Editor/GCAllocAnalyzerData.cs`**
Data structures including GraphFrameStore for full-range per-frame GC totals and cached analysis.

**`Runtime/GCAllocCaptureHelper.cs`** (~100 lines)
MonoBehaviour for on-device profiling. Enables managed call stacks at runtime, captures profiler data to `.raw` files.

### Namespaces

- `GCAllocBreakdown.Editor` — Editor tools (window + profiler module)
- `GCAllocBreakdown` — Runtime capture helper
- `GCAllocTest` — Test allocation generators (in Samples~)

### Key Unity Profiler APIs Used

- `ProfilerDriver.GetRawFrameDataView()` — per-frame profiler data access
- `FrameDataView` — marker extraction, call stack resolution
- `ProfilerModule` / `ProfilerModuleViewController` — Profiler integration

## Code Conventions

- **No LINQ in hot paths** — explicit loops to avoid allocations (this is a GC profiler, so it must not create GC pressure itself)
- **Reusable buffers** — shared `StringBuilder` (`m_SharedSB`), reused `List<T>` instances
- **Object pooling** — `DisplayRow` pooling in module details view
- **Pre-computed display strings** — computed once during extraction, not during UI bind
- **UserData-based callbacks** — no closures in UI bind methods to avoid allocations
- **Field naming** — `m_` prefix for private instance fields, `k_` for constants
- **Serialization** — `[SerializeField]` on key fields so state survives domain reloads
- **Section separators** — `// ═══` comment blocks to delineate code sections
- **Context menus** — use `AddManipulator(new ContextualMenuManipulator(...))`, NOT `RegisterCallback<ContextualMenuPopulateEvent>`
