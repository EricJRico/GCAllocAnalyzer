# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - 2026-03-29

### Added
- Per-frame graph: stacked colored segments showing top 16 allocation methods per frame (16-color palette, "Others" gray)
- Per-frame graph: overlay mode — select a marker to see its per-frame contribution highlighted on the graph
- Per-frame graph: overview strip with draggable viewport indicator, edge-resize zoom
- Per-frame graph: Y-axis drag zoom on Y-axis labels
- Per-frame graph: keyboard segment navigation (Up/Down arrows to cycle segments within a bar)
- Per-frame graph: Order by Size toggle (chronological vs size-descending)
- Top Offenders: Avg / Frame category alongside By Total Bytes and Largest Single Allocations
- Top Offenders: configurable top-N dropdown (1-10)
- Binary snapshot format (`.gcas`) with skeleton/heavy split for fast domain reload restore
- Background thread allocation restore — window appears instantly, full interactivity restored async
- HTML Compare Tool for side-by-side CSV diff (accessible via Export menu)
- Customizable graph colors: bar color, dim color, overlay highlight, highlight tint/outline, bar spacing
- No-callstack fallback: automatic hierarchy path resolution when Profiler call stacks aren't enabled
- Comprehensive README with animated GIFs for all graph controls and features

### Changed
- Snapshot format changed from JSON to binary `.gcas` (two-part: skeleton for instant UI, heavy payload for full data)
- Analysis performance: BuildGrouping 95% faster via integer-keyed array indexing (3,473ms → 178ms for 1.87M allocs)
- BarChart subsystem extracted into standalone `GCAllocBreakdown.BarChart` assembly (zero profiler dependencies, reusable)

### Fixed
- Bar selection now survives domain reload
- Layout persistence for graph pane height and right panel width

## [0.1.0] - 2026-03-01

### Added
- GC Alloc Analyzer Editor Window for multi-frame analysis with full call-stack resolution
- GC Alloc Breakdown Profiler Module for per-frame analysis inside Unity's Profiler window
- Per-frame graph with zoom/pan (WASD + mouse wheel) and drag-select sub-range analysis
- Grouping by full call stack or top frame, with sorting, filtering, and top-offenders views
- CSV export of marker table and individual allocations
- Save/load snapshot support
- Profiler sync: click individual allocations to jump to that frame in the Profiler's CPU module
- Customizable severity colors and byte thresholds via Preferences
- Sample: GC Alloc Test Rig with 11 allocation pattern generators
