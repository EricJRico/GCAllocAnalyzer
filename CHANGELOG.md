# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-03-01

### Added
- GC Alloc Analyzer Editor Window for multi-frame analysis with full call-stack resolution
- GC Alloc Breakdown Profiler Module for per-frame analysis inside Unity's Profiler window
- Per-frame graph with zoom/pan (WASD + mouse wheel), overview strip, and drag-select sub-range analysis
- Grouping by full call stack or top frame, with sorting, filtering, and top-offenders views
- CSV/JSON export of analysis results
- Save/load snapshot support
- Runtime GCAllocCaptureHelper component for on-device profiler capture
- Sample: GC Alloc Test Rig with 11 allocation pattern generators
