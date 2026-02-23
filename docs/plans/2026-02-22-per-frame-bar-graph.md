# Per-Frame Allocation Bar Graph Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a Profile-Analyzer-style per-frame bar chart to the left panel showing GC allocation bytes per frame, with statistical spike coloring, Y/X-axis scales, tooltips, click-to-frame, and a selected-group overlay.

**Architecture:** A collapsible `Foldout` inserted between the Filters foldout and the marker list header in `BuildLeftPanel()`. Uses one `VisualElement` per bar (or bucket when frames > pixels), with automatic bucketing to cap element count at container pixel width. The graph reads from the existing `m_Snapshot.PerFrameBytes` array and computes per-group overlays from `CallsiteGroup.Allocations`.

**Tech Stack:** Unity UIElements (`VisualElement`, `Label`, `Foldout`), C# 9.0

**File:** `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

---

### Task 1: Add Graph Constants and UI Field Declarations

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs:18-30` (CONSTANTS section)
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs:42-80` (UI FIELDS section)

**Step 1: Add graph constants after existing constants (after line 30)**

```csharp
const float GRAPH_HEIGHT = 120;
const float GRAPH_Y_AXIS_WIDTH = 52;
const float GRAPH_X_AXIS_HEIGHT = 16;
```

**Step 2: Add graph color constants after existing static colors (after line 39)**

```csharp
static readonly Color k_GraphBg = new(0.1f, 0.1f, 0.1f);
static readonly Color k_GraphGuideLine = new(0.2f, 0.2f, 0.2f);
static readonly Color k_GraphBarNormal = new(0.27f, 0.67f, 0.6f);    // teal #44AA99
static readonly Color k_GraphBarElevated = new(1f, 0.8f, 0.2f);      // yellow #FFCC33
static readonly Color k_GraphBarSpike = new(1f, 0.27f, 0.27f);       // red #FF4444
static readonly Color k_GraphOverlay = new(0.67f, 0.87f, 0.8f);      // light cyan #AADDCC
```

**Step 3: Add UI fields for the graph (after line 56, after `m_LoadBtn`)**

```csharp
// Per-frame graph
Foldout m_GraphFoldout;
VisualElement m_GraphRoot;          // holds Y-axis + chart area side-by-side
VisualElement m_GraphBarArea;       // dark background, bars live here
Label m_GraphYMax, m_GraphYMid;
Label m_GraphXStart, m_GraphXEnd;
Label m_GraphOverlayLabel;          // selected marker name, bottom-right of chart
```

**Step 4: Add graph data fields (after line 118, after `m_PerFrameBuffer`)**

```csharp
// Graph bucketing buffers
long[] m_GraphBuckets;
long[] m_GraphOverlayBuckets;
int m_GraphBucketCount;
int m_GraphFramesPerBucket;
bool m_GraphNeedsRebuild;
```

**Step 5: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat(graph): add constants and UI field declarations for per-frame bar graph"
```

---

### Task 2: Build the Graph UI Structure

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs` — new method `BuildPerFrameGraph()`, modify `BuildLeftPanel()` to insert it

**Step 1: Add `BuildPerFrameGraph()` method**

Place this after `BuildLeftPanel()` (after line 497). This builds the graph's structural elements but does not populate bars (that's `RebuildGraph()`).

```csharp
VisualElement BuildPerFrameGraph()
{
    m_GraphFoldout = MakeSectionFoldout("Per-Frame Graph");
    m_GraphFoldout.style.marginLeft = 4;
    m_GraphFoldout.style.marginRight = 4;
    m_GraphFoldout.style.marginTop = 0;
    m_GraphFoldout.style.display = DisplayStyle.None; // hidden until data

    m_GraphRoot = new VisualElement
    {
        style =
        {
            flexDirection = FlexDirection.Row,
            height = GRAPH_HEIGHT + GRAPH_X_AXIS_HEIGHT
        }
    };

    // Y-axis labels (left column)
    var yAxis = new VisualElement
    {
        style =
        {
            width = GRAPH_Y_AXIS_WIDTH,
            justifyContent = Justify.SpaceBetween,
            alignItems = Align.FlexEnd,
            paddingRight = 4,
            height = GRAPH_HEIGHT
        }
    };
    m_GraphYMax = new Label("—") { style = { fontSize = 10, color = k_DimGray, unityTextAlign = TextAnchor.MiddleRight } };
    m_GraphYMid = new Label("—") { style = { fontSize = 10, color = k_DimGray, unityTextAlign = TextAnchor.MiddleRight } };
    var yZero = new Label("0") { style = { fontSize = 10, color = k_DimGray, unityTextAlign = TextAnchor.MiddleRight } };
    yAxis.Add(m_GraphYMax);
    yAxis.Add(m_GraphYMid);
    yAxis.Add(yZero);
    m_GraphRoot.Add(yAxis);

    // Chart area (right side, fills remaining width)
    var chartColumn = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };

    // Bar area — dark background, bars anchored to bottom
    m_GraphBarArea = new VisualElement
    {
        style =
        {
            flexGrow = 0,
            height = GRAPH_HEIGHT,
            backgroundColor = k_GraphBg,
            flexDirection = FlexDirection.Row,
            alignItems = Align.FlexEnd,
            overflow = Overflow.Hidden,
            borderBottomWidth = 1,
            borderBottomColor = k_GraphGuideLine
        }
    };
    m_GraphBarArea.RegisterCallback<GeometryChangedEvent>(OnGraphGeometryChanged);
    chartColumn.Add(m_GraphBarArea);

    // X-axis labels row
    var xAxis = new VisualElement
    {
        style =
        {
            flexDirection = FlexDirection.Row,
            justifyContent = Justify.SpaceBetween,
            height = GRAPH_X_AXIS_HEIGHT
        }
    };
    m_GraphXStart = new Label("—") { style = { fontSize = 10, color = k_DimGray } };
    m_GraphXEnd = new Label("—") { style = { fontSize = 10, color = k_DimGray } };
    xAxis.Add(m_GraphXStart);
    xAxis.Add(m_GraphXEnd);
    chartColumn.Add(xAxis);

    m_GraphRoot.Add(chartColumn);

    // Overlay label (selected marker name) — positioned at bottom-right of bar area
    m_GraphOverlayLabel = new Label("")
    {
        style =
        {
            position = Position.Absolute,
            bottom = GRAPH_X_AXIS_HEIGHT + 2,
            right = 4,
            fontSize = 10,
            color = k_GraphOverlay,
            unityTextAlign = TextAnchor.LowerRight
        }
    };
    m_GraphRoot.Add(m_GraphOverlayLabel);

    m_GraphFoldout.Add(m_GraphRoot);
    return m_GraphFoldout;
}

void OnGraphGeometryChanged(GeometryChangedEvent evt)
{
    if (!m_Snapshot.HasData || m_Snapshot.PerFrameBytes == null) return;
    // Only rebuild if width actually changed meaningfully (>2px)
    float oldW = evt.oldRect.width;
    float newW = evt.newRect.width;
    if (Mathf.Abs(newW - oldW) < 2f) return;
    RebuildGraph();
}
```

**Step 2: Insert graph into `BuildLeftPanel()` between filters and marker headers**

In `BuildLeftPanel()`, after `left.Add(m_FiltersFoldout);` (line 475) and before `m_MarkerHeaderRow = BuildMarkerHeaders();` (line 478), add:

```csharp
left.Add(BuildPerFrameGraph());
```

**Step 3: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat(graph): build per-frame graph UI structure in left panel"
```

---

### Task 3: Implement `RebuildGraph()` — Bucketing and Bar Creation

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs` — add `RebuildGraph()` method

**Step 1: Add `RebuildGraph()` after `OnGraphGeometryChanged()`**

```csharp
void RebuildGraph()
{
    if (m_GraphBarArea == null) return;

    var perFrame = m_Snapshot.PerFrameBytes;
    if (perFrame == null || perFrame.Length == 0)
    {
        m_GraphFoldout.style.display = DisplayStyle.None;
        return;
    }

    m_GraphFoldout.style.display = DisplayStyle.Flex;

    int frameCount = perFrame.Length;
    float areaWidth = m_GraphBarArea.resolvedStyle.width;
    if (areaWidth < 1f) areaWidth = 400f; // fallback before first layout

    // Bucketing: cap bar count at pixel width
    m_GraphBucketCount = frameCount;
    m_GraphFramesPerBucket = 1;
    if (frameCount > (int)areaWidth)
    {
        m_GraphBucketCount = Mathf.Max(1, (int)areaWidth);
        m_GraphFramesPerBucket = Mathf.CeilToInt((float)frameCount / m_GraphBucketCount);
    }

    // Ensure bucket buffer
    if (m_GraphBuckets == null || m_GraphBuckets.Length < m_GraphBucketCount)
        m_GraphBuckets = new long[m_GraphBucketCount];
    else
        Array.Clear(m_GraphBuckets, 0, m_GraphBucketCount);

    // Fill buckets (max of frames in each bucket)
    long maxValue = 0;
    double sum = 0;
    for (int b = 0; b < m_GraphBucketCount; b++)
    {
        int startIdx = b * m_GraphFramesPerBucket;
        int endIdx = Mathf.Min(startIdx + m_GraphFramesPerBucket, frameCount);
        long bucketMax = 0;
        for (int i = startIdx; i < endIdx; i++)
        {
            if (perFrame[i] > bucketMax) bucketMax = perFrame[i];
        }
        m_GraphBuckets[b] = bucketMax;
        sum += bucketMax;
        if (bucketMax > maxValue) maxValue = bucketMax;
    }

    // Compute mean and stddev for color thresholds
    double mean = sum / m_GraphBucketCount;
    double varianceSum = 0;
    for (int b = 0; b < m_GraphBucketCount; b++)
    {
        double diff = m_GraphBuckets[b] - mean;
        varianceSum += diff * diff;
    }
    double stddev = Math.Sqrt(varianceSum / m_GraphBucketCount);
    double threshold1 = mean + stddev;
    double threshold2 = mean + 2 * stddev;

    // Update Y-axis labels
    m_GraphYMax.text = FormatBytes(maxValue);
    m_GraphYMid.text = FormatBytes(maxValue / 2);

    // Update X-axis labels
    m_GraphXStart.text = m_Snapshot.FrameStart.ToString();
    m_GraphXEnd.text = m_Snapshot.FrameEnd.ToString();

    // Clear old bars
    m_GraphBarArea.Clear();

    float barWidth = areaWidth / m_GraphBucketCount;
    float maxHeight = GRAPH_HEIGHT;

    // Add horizontal guide lines (mid-point)
    var guideMid = new VisualElement
    {
        style =
        {
            position = Position.Absolute,
            left = 0, right = 0,
            bottom = maxHeight / 2,
            height = 1,
            backgroundColor = k_GraphGuideLine
        }
    };
    m_GraphBarArea.Add(guideMid);

    for (int b = 0; b < m_GraphBucketCount; b++)
    {
        long val = m_GraphBuckets[b];
        float height = maxValue > 0 ? (float)val / maxValue * maxHeight : 0;

        // Color based on statistical threshold
        Color barColor;
        if (val > threshold2) barColor = k_GraphBarSpike;
        else if (val > threshold1) barColor = k_GraphBarElevated;
        else barColor = k_GraphBarNormal;

        // Outer bar element
        var bar = new VisualElement
        {
            style =
            {
                width = Mathf.Max(barWidth, 1f),
                height = Mathf.Max(height, val > 0 ? 1f : 0f),
                backgroundColor = barColor,
                flexShrink = 0
            },
            userData = b // bucket index for click handler
        };

        // Inner overlay element (hidden by default)
        var overlay = new VisualElement
        {
            name = "overlay",
            style =
            {
                width = Length.Percent(100),
                height = 0,
                backgroundColor = k_GraphOverlay,
                position = Position.Absolute,
                bottom = 0
            }
        };
        bar.Add(overlay);

        // Tooltip
        int frameStart = m_Snapshot.FrameStart + b * m_GraphFramesPerBucket;
        int frameEnd = Mathf.Min(frameStart + m_GraphFramesPerBucket - 1, m_Snapshot.FrameEnd);
        if (m_GraphFramesPerBucket == 1)
            bar.tooltip = string.Concat("Frame ", frameStart.ToString(), ": ", FormatBytes(val));
        else
            bar.tooltip = string.Concat("Frames ", frameStart.ToString(), "\u2013", frameEnd.ToString(), ": ", FormatBytes(val), " (max)");

        // Click handler
        bar.RegisterCallback<ClickEvent>(OnGraphBarClicked);

        m_GraphBarArea.Add(bar);
    }

    // Clear overlay since selection may no longer be valid
    m_GraphOverlayLabel.text = "";
}

void OnGraphBarClicked(ClickEvent evt)
{
    var bar = evt.currentTarget as VisualElement;
    if (bar?.userData is not int bucketIdx) return;

    // Find the frame with max allocation in this bucket
    var perFrame = m_Snapshot.PerFrameBytes;
    if (perFrame == null) return;

    int startIdx = bucketIdx * m_GraphFramesPerBucket;
    int endIdx = Mathf.Min(startIdx + m_GraphFramesPerBucket, perFrame.Length);

    int maxIdx = startIdx;
    long maxVal = 0;
    for (int i = startIdx; i < endIdx; i++)
    {
        if (perFrame[i] > maxVal) { maxVal = perFrame[i]; maxIdx = i; }
    }

    int frameIndex = m_Snapshot.FrameStart + maxIdx;

    // Jump to frame in Profiler
    EnsureProfilerRef();
    if (m_ProfilerWindow != null)
    {
        try { m_ProfilerWindow.selectedFrameIndex = frameIndex; }
        catch (Exception e)
        {
            Debug.LogWarning(string.Concat("[GC Alloc Analyzer] Graph click frame=", frameIndex.ToString(), ": ", e.Message));
        }
    }
}
```

**Step 2: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat(graph): implement RebuildGraph with bucketing, colors, tooltips, click"
```

---

### Task 4: Implement `UpdateGraphOverlay()` for Selected Group

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs` — add `UpdateGraphOverlay()`, call from `OnMarkerSelectionChanged()`

**Step 1: Add `UpdateGraphOverlay()` after `OnGraphBarClicked()`**

```csharp
void UpdateGraphOverlay(CallsiteGroup group)
{
    if (m_GraphBarArea == null || m_Snapshot.PerFrameBytes == null) return;

    if (group == null)
    {
        // Clear all overlays
        ClearGraphOverlay();
        return;
    }

    int frameCount = m_Snapshot.PerFrameBytes.Length;

    // Ensure overlay bucket buffer
    if (m_GraphOverlayBuckets == null || m_GraphOverlayBuckets.Length < m_GraphBucketCount)
        m_GraphOverlayBuckets = new long[m_GraphBucketCount];
    else
        Array.Clear(m_GraphOverlayBuckets, 0, m_GraphBucketCount);

    // Build per-frame bytes for this group
    // Reuse m_PerFrameBuffer (safe — not called during grouping)
    EnsurePerFrameBuffer(frameCount);
    for (int i = 0; i < group.Allocations.Count; i++)
    {
        var alloc = group.Allocations[i];
        int idx = alloc.FrameIndex - m_Snapshot.FrameStart;
        if (idx >= 0 && idx < frameCount)
            m_PerFrameBuffer[idx] += alloc.Bytes;
    }

    // Bucket the group's per-frame data (same bucketing as main graph)
    for (int b = 0; b < m_GraphBucketCount; b++)
    {
        int startIdx = b * m_GraphFramesPerBucket;
        int endIdx = Mathf.Min(startIdx + m_GraphFramesPerBucket, frameCount);
        long bucketMax = 0;
        for (int i = startIdx; i < endIdx; i++)
        {
            if (m_PerFrameBuffer[i] > bucketMax) bucketMax = m_PerFrameBuffer[i];
        }
        m_GraphOverlayBuckets[b] = bucketMax;
    }

    // Update overlay elements on existing bars
    // The bar area children are: [0] = guide line, [1..N] = bars
    long maxValue = 0;
    for (int b = 0; b < m_GraphBucketCount; b++)
        if (m_GraphBuckets[b] > maxValue) maxValue = m_GraphBuckets[b];

    int childOffset = 1; // skip guide line element
    for (int b = 0; b < m_GraphBucketCount; b++)
    {
        int childIdx = childOffset + b;
        if (childIdx >= m_GraphBarArea.childCount) break;

        var bar = m_GraphBarArea[childIdx];
        var overlay = bar.Q("overlay");
        if (overlay == null) continue;

        long overlayVal = m_GraphOverlayBuckets[b];
        if (overlayVal <= 0 || maxValue <= 0)
        {
            overlay.style.height = 0;
            continue;
        }

        float overlayHeight = (float)overlayVal / maxValue * GRAPH_HEIGHT;
        overlay.style.height = Mathf.Max(overlayHeight, 1f);
    }

    // Update overlay label
    m_GraphOverlayLabel.text = group.DisplayName;
}

void ClearGraphOverlay()
{
    if (m_GraphBarArea == null) return;

    int childOffset = 1; // skip guide line
    for (int i = childOffset; i < m_GraphBarArea.childCount; i++)
    {
        var overlay = m_GraphBarArea[i].Q("overlay");
        if (overlay != null) overlay.style.height = 0;
    }
    m_GraphOverlayLabel.text = "";
}
```

**Step 2: Wire `UpdateGraphOverlay` into `OnMarkerSelectionChanged()`**

At line 1501, after `UpdateMarkerSummary(group);`, add:

```csharp
UpdateGraphOverlay(group);
```

Also update the null-group path at line 1500 — in `ClearMarkerSummary()` or inline:

Change:
```csharp
if (group == null) { ClearMarkerSummary(); return; }
```
To:
```csharp
if (group == null) { ClearMarkerSummary(); ClearGraphOverlay(); return; }
```

**Step 3: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat(graph): add selected-group overlay to per-frame bar graph"
```

---

### Task 5: Wire Rebuild Triggers into Analysis, Load, and Restore Flows

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs` — add `RebuildGraph()` calls to 3 locations

**Step 1: Add `RebuildGraph()` call at end of `RunAnalysis()`**

After line 1142 (`ShowNoDataState(m_ActiveGroups.Count == 0);`), before the status bar update, add:

```csharp
RebuildGraph();
```

**Step 2: Add `RebuildGraph()` call at end of `OnLoadSnapshot()`**

After line 373 (`ShowNoDataState(m_ActiveGroups.Count == 0);`), add:

```csharp
RebuildGraph();
```

**Step 3: Add `RebuildGraph()` call at end of `TryRestoreAfterReload()`**

After line 192 (`ShowNoDataState(m_ActiveGroups.Count == 0);`), add:

```csharp
RebuildGraph();
```

**Step 4: Update `ShowNoDataState()` to hide graph when no data**

In `ShowNoDataState(bool show)` (around line 1510), add:

```csharp
if (m_GraphFoldout != null)
    m_GraphFoldout.style.display = show ? DisplayStyle.None : DisplayStyle.Flex;
```

**Step 5: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat(graph): wire RebuildGraph into analysis, load, and restore flows"
```

---

### Task 6: Manual Testing and Polish

**Step 1: Open Unity, create test rig, run profiler, analyze**

1. Open Unity project (6000.3.6f1)
2. `Tools > GC Alloc Test > Create Test Rig`
3. Enter Play Mode, let it run 100+ frames
4. Open Profiler (`Ctrl+7`), enable `Call Stacks > GC.Alloc`
5. Open analyzer: `Window > Analysis > GC Alloc Analyzer`
6. Set frame range and click Analyze

**Step 2: Verify graph appears**

- Per-Frame Graph foldout should appear between Filters and marker list
- Bars should be visible with teal/yellow/red coloring
- Y-axis should show max and mid-point byte labels
- X-axis should show frame start and end numbers

**Step 3: Verify interactions**

- Hover a bar → tooltip shows "Frame N: X KB"
- Click a bar → Profiler window jumps to that frame
- Click a marker in the list → overlay appears on graph in light cyan
- Click a different marker → overlay updates
- Deselect → overlay clears

**Step 4: Verify edge cases**

- Collapse and expand the graph foldout
- Save snapshot → close window → reopen → Load snapshot → graph restores
- Try with a small frame range (1-5 frames) — bars should be wide
- Try with a large frame range (500+ frames) — bucketing should kick in
- Domain reload (save a script) → graph should restore

**Step 5: Fix any visual issues discovered during testing**

Common issues to watch for:
- Bars not touching the bottom (check `alignItems = FlexEnd` on bar area)
- Guide line not visible (check z-order — it's the first child, may need higher z-index or `position = Absolute`)
- Overlay label overlapping bars (check absolute positioning)
- Bar width rounding leaving gaps (ensure `flexShrink = 0` on bars)

**Step 6: Commit final state**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: add per-frame allocation bar graph (Task 4)"
```

---

## Summary

| Task | Description | Estimated Scope |
|------|-------------|----------------|
| 1 | Constants + field declarations | ~25 lines added |
| 2 | Graph UI structure (`BuildPerFrameGraph()`) | ~90 lines added |
| 3 | `RebuildGraph()` + bar creation + click handler | ~130 lines added |
| 4 | `UpdateGraphOverlay()` + wiring to selection | ~80 lines added |
| 5 | Wire rebuild triggers (3 call sites + ShowNoDataState) | ~5 lines added |
| 6 | Manual testing and polish | 0 lines (fixes only) |

**Total:** ~330 lines of new code in one file.
