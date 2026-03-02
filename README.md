# GC Alloc Analyzer

[![Unity 6000.0+](https://img.shields.io/badge/Unity-6000.0%2B-blue.svg)](https://unity.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

A Unity Editor tool for profiling and analyzing garbage collection allocations. Integrates with Unity's Profiler as both a standalone Editor Window (multi-frame analysis) and a Profiler Module (per-frame breakdown). Includes a runtime helper for capturing profiler data on-device.

<!-- TODO: Replace with actual screenshot/GIF of the main analyzer window -->
<!-- ![GC Alloc Analyzer](Documentation~/images/analyzer-overview.png) -->

## Quick Install

In Unity's Package Manager, click **+** > **Add package from git URL** and enter:

```
https://github.com/EricJRico/GCAllocAnalyzer.git
```

> See [Installation](#installation) for local development setup.

---

## Features

### Analyzer Window

Open via **Window > Analysis > GC Alloc Analyzer**.

Multi-frame analysis across any range of profiled frames. Pull lightweight per-frame GC totals from the Profiler, then run a full call-stack extraction to see exactly where allocations come from.

<!-- TODO: Screenshot of the analyzer window with data -->

- **Call-stack grouping** — group allocations by full call stack or by top-level method
- **Filtering** — filter by name, exclude patterns, or specific threads
- **Sortable columns** — total bytes, count, average, percentage, per-frame statistics (median, mean, min, max, range)
- **Top offenders** — ranked views of the worst allocation sites and largest individual allocations
- **Color-coded severity** — red (10 KB+), yellow (1 KB+), gray (< 1 KB)
- **Source navigation** — click to open the allocating method in your IDE at the exact line

### Per-Frame Graph

Interactive bar chart showing GC allocations per frame with full zoom, pan, and selection controls.

<!-- TODO: GIF showing zoom/pan with WASD + mouse wheel -->
<!-- ![Per-Frame Graph](Documentation~/images/graph-zoom-pan.gif) -->

- **Zoom & pan** — mouse wheel to zoom, WASD keys or drag the overview strip to navigate
- **Drag-select** — drag across bars to instantly re-analyze a sub-range (cached, no re-read from Profiler)
- **Overlay mode** — select an allocation site to see its per-frame contribution overlaid on the graph
- **Overview strip** — miniature full-range view with viewport indicator for quick navigation
- **Sort toggle** — switch between chronological frame order and size-descending order
- **Keyboard-driven selection** — arrow keys to move, +/- to grow/shrink selection, Enter to analyze

<!-- TODO: GIF showing drag-select sub-range analysis -->
<!-- ![Drag Select](Documentation~/images/graph-drag-select.gif) -->

### Profiler Module

A dedicated **GC Alloc Breakdown** module inside Unity's Profiler window for per-frame analysis.

<!-- TODO: Screenshot of the Profiler module view -->

- **Per-frame breakdown** — see allocation sites for the currently selected Profiler frame
- **Expandable call stacks** — click to expand full call stack inline
- **LRU cache** — results for up to 512 frames are cached for instant scrubbing
- **Sortable columns** — bytes, count, average, and allocation site name

### Runtime Capture

The `GCAllocCaptureHelper` MonoBehaviour enables on-device profiler capture for analyzing GC allocations on target hardware.

- **Managed call stacks** — automatically enables `Profiler.enableAllocationCallstacks` at startup
- **Raw file capture** — writes `.raw` profiler data to `Application.persistentDataPath`
- **Toggle key** — start/stop capture with a configurable key (default: F9)
- **Configurable memory** — set `Profiler.maxUsedMemory` to prevent truncation on long sessions

Retrieve captured data via:
- **Android**: `adb pull /sdcard/Android/data/{bundleId}/files/profiler_capture.raw`
- **iOS**: Xcode Devices > Download Container > `AppData/Documents/`

### Save, Load & Export

- **Save/Load snapshots** — save analysis results as `.json` files for sharing or later review. Load snapshots without needing the original Profiler data.
- **Export Marker Table CSV** — export the filtered/sorted allocation site table with all statistics
- **Export Individual Allocations CSV** — export every raw `GC.Alloc` event with full call stacks

---

## Installation

### Via Git URL (recommended)

1. Open Unity's **Package Manager** (Window > Package Manager)
2. Click **+** > **Add package from git URL**
3. Enter: `https://github.com/EricJRico/GCAllocAnalyzer.git`

### Local Development

Clone the repo and reference it from a separate Unity project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.ericjrico.gcalloc-analyzer": "file:../GCAllocAnalyzer"
  }
}
```

---

## Quick Start

1. **Record profiler data** — Enter Play Mode and let your scene run. Open the Profiler (`Ctrl+7`) and enable **Call Stacks > GC.Alloc** in the Profiler toolbar for detailed call-stack resolution.
2. **Open the analyzer** — Go to **Window > Analysis > GC Alloc Analyzer**.
3. **Pull Data** — Click **Pull Data** to read per-frame GC totals from the Profiler. This populates the per-frame graph.
4. **Analyze** — Set your frame range and click **Analyze** for full call-stack extraction.
5. **Explore** — Sort, filter, and click allocation sites to see call stacks, per-frame overlays, and individual allocations. Drag-select on the graph to analyze sub-ranges instantly.

> **Tip:** For the best results, enable **Call Stacks > GC.Alloc** in the Profiler toolbar *before* entering Play Mode. Without call stacks, you'll see hierarchy paths instead of resolved method names.

---

## Keyboard Shortcuts

Graph shortcuts are active when the graph is focused (automatic on hover).

| Input | Action |
|-------|--------|
| `Mouse Wheel` | Zoom in / out (centered on cursor) |
| `W` / `S` | Zoom in / out (centered on cursor) |
| `A` / `D` | Pan left / right |
| `Left` / `Right` | Navigate one bar at a time |
| `Shift+Left` / `Shift+Right` | Navigate 10 bars at a time |

---

## Requirements

- **Unity 6000.0+**
- **Profiler data** — requires an active or loaded Profiler session
- **Call stacks** — enable **Call Stacks > GC.Alloc** in the Profiler toolbar for full call-stack resolution (otherwise falls back to hierarchy paths)

## License

[MIT](LICENSE) — Copyright (c) 2025 Eric J Rico
