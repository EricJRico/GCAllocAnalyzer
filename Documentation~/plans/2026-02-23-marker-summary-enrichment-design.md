# Task 6: Marker Summary Enrichment — Design

## Goal

Enrich the right-panel "Selected Allocation Site" section with per-frame statistics, clickable frame navigation links, and top worst frames. All underlying data already exists on `CallsiteGroup` from Task 3; this task surfaces it in the UI.

## Current State

`UpdateMarkerSummary()` shows:
- Marker name (bold) + source file:line
- Single stats line: `Total: X   Count: Y   Avg: Z   W% of total`
- Call stack (scrollable)
- Individual allocations (ListView)

## Target State

```
MarkerName (bold)
source.cs:42

Total: 48.2 KB    Count: 234    Avg: 206 B    12.3% of total

── Per-Frame Statistics ──
First seen: frame 142
Median/Frame: 512 B    Mean/Frame: 823 B
Min: 64 B (frame 203) [click]    Max: 4.1 KB (frame 377) [click]

── Top 3 Worst Frames ──
1. frame 377 — 4.1 KB [click]
2. frame 291 — 3.8 KB [click]
3. frame 155 — 2.9 KB [click]

── Call Stack ──
(existing)

── Individual Allocations ──
(existing)
```

## Data Model Change

Add to `CallsiteGroup`:
```csharp
public (int Frame, long Bytes)[] TopWorstFrames; // length 0–3
```

Populated at end of `ComputePerFrameStats()` by tracking top-3 values during the existing per-frame scan. No additional pass needed.

## Frame Navigation

New lightweight helper `NavigateToFrame(int frameIndex)` that sets `m_ProfilerWindow.selectedFrameIndex`. Does not use full `SelectInCpuModule` since aggregated per-frame data has no specific sample index.

## UI Elements

New fields created in `BuildRightPanel()`, inserted between `m_MarkerStatsLabel` and the Call Stack section:

- `m_PerFrameStatsContainer` — contains First Frame label, Median/Mean label, Min/Max labels (Min and Max are clickable)
- `m_TopWorstContainer` — contains up to 3 clickable frame labels

Both containers are section-styled with `── Title ──` headers matching existing code patterns.

Populated in `UpdateMarkerSummary()` using pre-formatted strings from `CallsiteGroup` (FormattedMedian, FormattedMean, FormattedMin, FormattedMax) and new formatted top-worst strings.

Click handlers use `userData` pattern (no closures) consistent with codebase conventions.

## Decisions

- **Top 3 computed during grouping** (not at display time) — zero cost per click, tiny memory overhead
- **No distribution visualization** — keeping scope tight to text + clickable links
- **Section separators** use `── Title ──` label pattern for visual grouping
