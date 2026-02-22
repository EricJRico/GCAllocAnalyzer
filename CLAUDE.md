# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

GCAllocAnalyzer is a Unity Editor tool for profiling and analyzing garbage collection (GC) allocations. It integrates with Unity's Profiler as both a standalone Editor Window (multi-frame analysis) and a Profiler Module (per-frame analysis). A runtime helper component enables capturing profiler data on devices.

**Unity Version**: 6000.3.6f1
**Language**: C# 9.0 / .NET 4.7.1

## Build & Development

This is a Unity project — there is no standalone build command. Open the project in Unity 6000.3.6f1 and scripts compile automatically. Assembly definitions control compilation:

- `GCAllocBreakdown.asmdef` — Runtime assembly (contains `GCAllocCaptureHelper`)
- `GCAllocBreakdown.Editor.asmdef` — Editor-only assembly (references runtime)

Command-line build (outside Unity): `dotnet build GCAllocAnalyzer.sln`

## Testing

No automated test suite. Testing is manual via scene-based allocation generators:

1. In Unity: `Tools > GC Alloc Test > Create Test Rig` (adds all test components)
2. Enter Play Mode, let it run 100+ frames
3. Open Profiler (`Ctrl+7`), enable `Call Stacks → GC.Alloc` in toolbar
4. Open analyzer: `Window > Analysis > GC Alloc Analyzer`
5. Set frame range and click Analyze

Test generators are in `Assets/GCAllocTests/` — they produce various allocation patterns (string ops, boxing, closures, coroutines, events, varied sizes).

## Architecture

### Three Core Source Files

**`Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`** (~2100 lines)
Main EditorWindow for multi-frame analysis. Extracts GC.Alloc events from profiler data across a frame range, groups by full call stack or top frame, provides sorting/filtering/top-offenders views. Uses UIElements with virtualized ListViews.

**`Assets/GCAllocAnalyzer/Editor/GCAllocBreakdownModule.cs`** (~750 lines)
ProfilerModule + ProfilerModuleViewController for per-frame breakdown inside Unity's Profiler window. Uses an LRU cache (512 frames) for instant frame switching. Shares extraction logic patterns with the main window but operates per-frame.

**`Assets/GCAllocAnalyzer/Runtime/GCAllocCaptureHelper.cs`** (~100 lines)
MonoBehaviour for on-device profiling. Enables managed call stacks at runtime, captures profiler data to `.raw` files that can be pulled from device storage and loaded in the Editor analyzer.

### Namespaces

- `GCAllocBreakdown.Editor` — Editor tools (window + profiler module)
- `GCAllocBreakdown` — Runtime capture helper
- `GCAllocTest` — Test allocation generators

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
