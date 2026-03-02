# Graph Architecture Redesign — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the current per-frame graph with a full-range graph featuring cached analysis, auto-analyze on drag-select, analyzed-range dimming, a Perfetto-style two-level layout (overview strip + zoomable detail view), and WASD navigation.

**Architecture:** The graph always displays all profiler frames (from Pull Data). A new `GraphFrameStore` class caches per-frame GC totals and raw allocations. First Analyze is expensive (Profiler extraction); subsequent sub-range analyses filter from the cache and are near-instant. A two-level layout (overview strip + zoomable detail view) provides context at all zoom levels. Un-analyzed bars render dimmed at ~30% opacity.

**Tech Stack:** Unity 6000.3.6f1, C# 9.0, UIElements with Painter2D rendering, ProfilerDriver API.

**Design doc:** `docs/plans/2026-03-01-graph-architecture-redesign.md`

**Conventions:** No LINQ in hot paths. `m_` prefix for private instance fields, `k_` for constants. Reusable buffers. `// ═══` section separators. No closures in UI bind callbacks.

---

## Dependency Graph & Parallelism

```
Task 1 (GraphFrameStore)  ──┬──→ Task 3 (Pull Data fast scan) → Task 5 (Cache in RunAnalysis)
                             │                                        ↓
Task 2 (Dimming in          │                                   Task 6 (RebuildFromCache)
  GraphElement)  ────────────┘                                   ↓           ↓
        ↓                                                   Task 7        Task 8
Task 4 (Controller                                       (Auto-analyze) (Reset btn)
  dimming wiring)                                               ↓           ↓
        ↓                                                   Task 9 (Viewport state + zoom/pan)
        └──────────────────────────────────────────────→        ↓
                                                         Task 10 ║ Task 11
                                                      (Overview) ║ (Scroller)
                                                              ↓      ↓
                                                         Task 12 (Integration polish)
                                                              ↓
                                                         Task 13 (Commit + verify)
```

**Parallel pairs:**
- Tasks 1 + 2 (independent data class vs. rendering change)
- Tasks 7 + 8 (auto-analyze vs. reset button, both depend on Task 6)
- Tasks 10 + 11 (overview strip vs. scroller, both depend on Task 9)

---

## Task 1: Create GraphFrameStore Data Class

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerData.cs` (add class at end of file, before closing namespace brace)

**Goal:** Add the data class that holds full-range graph data and the cached analysis, separate from `AnalysisSnapshot`.

**Step 1: Add the GraphFrameStore class**

Add this at the end of `GCAllocAnalyzerData.cs`, before the closing `}` of the `GCAllocBreakdown.Editor` namespace (after `GCAllocUtils`):

```csharp
    // ═══════════════════════════════════════════════════
    //  GRAPH FRAME STORE — full-range data + analysis cache
    //  Source of truth for the graph; separate from
    //  AnalysisSnapshot which represents the current view.
    // ═══════════════════════════════════════════════════

    [Serializable]
    internal class GraphFrameStore
    {
        public int FullFrameStart;
        public int FullFrameEnd;
        public long[] FullFrameBytes;              // per-frame GC totals for entire profiler range

        [NonSerialized] public List<RawAllocation> CachedRawAllocations;
        [NonSerialized] public List<string> CachedSortedThreadNames;

        public bool HasFullFrameData => FullFrameBytes != null && FullFrameBytes.Length > 0;
        public bool HasCachedAnalysis => CachedRawAllocations != null && CachedRawAllocations.Count > 0;

        public int FullFrameCount => HasFullFrameData ? FullFrameEnd - FullFrameStart + 1 : 0;

        public void Clear()
        {
            FullFrameStart = 0;
            FullFrameEnd = 0;
            FullFrameBytes = null;
            CachedRawAllocations = null;
            CachedSortedThreadNames = null;
        }

        public void CacheAnalysis(List<RawAllocation> rawAllocations, List<string> sortedThreadNames)
        {
            // Deep copy so the cache is independent of the snapshot
            CachedRawAllocations = new List<RawAllocation>(rawAllocations.Count);
            for (int i = 0; i < rawAllocations.Count; i++)
                CachedRawAllocations.Add(rawAllocations[i]);

            CachedSortedThreadNames = new List<string>(sortedThreadNames.Count);
            for (int i = 0; i < sortedThreadNames.Count; i++)
                CachedSortedThreadNames.Add(sortedThreadNames[i]);
        }

        public void EnsureNonSerializedLists()
        {
            // After domain reload, non-serialized fields are null.
            // We don't try to restore them — a fresh Analyze is needed.
        }
    }
```

**Step 2: Verify**

Run: `dotnet build GCAllocAnalyzer.sln` — should compile with zero errors.

**Step 3: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerData.cs
git commit -m "feat: add GraphFrameStore data class for full-range graph + cache"
```

---

## Task 2: Add Analyzed-Range Dimming to GraphElement

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GraphElement.cs`

**Goal:** GraphElement can render bars at ~30% opacity when they are outside the analyzed range. This is a rendering-only change — no controller/window changes yet.

**Step 1: Add dimmed color and analyzed-range fields**

In the `COLORS` section (after `k_OverlayColor`), add:

```csharp
        static readonly Color k_BarDimmed = new Color(0.27f, 0.67f, 0.6f, 0.3f);
```

In the `SELECTION STATE` section (after `m_HighlightedBar`), add:

```csharp
        int m_AnalyzedStartBar = -1;
        int m_AnalyzedEndBar   = -1;
```

**Step 2: Add SetAnalyzedRange method**

In the `PUBLIC DATA SETTERS` section (after `SetHighlightedBar`), add:

```csharp
        public void SetAnalyzedRange(int startBar, int endBar)
        {
            if (m_AnalyzedStartBar == startBar && m_AnalyzedEndBar == endBar) return;
            m_AnalyzedStartBar = startBar;
            m_AnalyzedEndBar = endBar;
            MarkDirtyRepaint();
        }
```

**Step 3: Modify DrawBars to apply dimming**

Replace the color-determination block inside `DrawBars` (the `for` loop body where color is chosen). The current code:

```csharp
                // Determine bar color
                Color color;
                if (i == m_HighlightedBar)
                    color = k_BarHighlighted;
                else if (sStart >= 0 && i >= sStart && i <= sEnd)
                    color = k_BarSelected;
                else
                    color = k_BarNormal;
```

Replace with:

```csharp
                // Determine bar color — dimming for bars outside analyzed range
                bool inAnalyzed = m_AnalyzedStartBar < 0
                    || (i >= m_AnalyzedStartBar && i <= m_AnalyzedEndBar);
                Color color;
                if (i == m_HighlightedBar)
                    color = k_BarHighlighted;
                else if (sStart >= 0 && i >= sStart && i <= sEnd)
                    color = k_BarSelected;
                else if (!inAnalyzed)
                    color = k_BarDimmed;
                else
                    color = k_BarNormal;
```

**Step 4: Skip overlay for bars outside analyzed range**

In `DrawOverlayBars`, add a check at the start of the loop body (after `if (barHeight <= 0f) continue;`):

```csharp
                // Skip overlay for bars outside analyzed range
                if (m_AnalyzedStartBar >= 0 && (i < m_AnalyzedStartBar || i > m_AnalyzedEndBar))
                    continue;
```

**Step 5: Verify**

Run: `dotnet build GCAllocAnalyzer.sln` — should compile with zero errors. Existing behavior unchanged because `m_AnalyzedStartBar` defaults to `-1` (all bars treated as analyzed).

**Step 6: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GraphElement.cs
git commit -m "feat: add analyzed-range dimming support to GraphElement"
```

---

## Task 3: Implement Pull Data Fast Scan

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Goal:** Pull Data now scans every profiler frame and sums GC.Alloc byte sizes per frame (without call stack resolution), populating `GraphFrameStore.FullFrameBytes`. The graph shows all bars dimmed (nothing analyzed yet).

**Step 1: Add m_FrameStore field**

In the `DATA FIELDS` section (around line 132, after `m_Snapshot`), add:

```csharp
        [SerializeField] GraphFrameStore m_FrameStore = new();
```

**Step 2: Rewrite OnPullData**

Replace the entire `OnPullData()` method (currently lines ~1011-1033) with:

```csharp
        void OnPullData()
        {
            int first = ProfilerDriver.firstFrameIndex;
            int last = ProfilerDriver.lastFrameIndex;
            if (first < 0 || last < 0 || last < first)
            {
                m_StatusLabel.text = "No profiler data available. Capture or load data first.";
                return;
            }

            // Clear previous state
            m_FrameStore.Clear();
            m_Snapshot.RawAllocations.Clear();
            m_Snapshot.TotalBytes = 0;
            m_Snapshot.TotalCount = 0;
            m_Snapshot.PerFrameBytes = null;
            m_Snapshot.GroupsByFullCallstack?.Clear();
            m_Snapshot.GroupsByTopFrame?.Clear();
            m_FilteredGroups.Clear();
            m_ActiveGroups = null;
            ShowNoDataState(true);
            ClearGraphOverlay();

            int totalFrames = last - first + 1;
            long[] fullFrameBytes = new long[totalFrames];

            try
            {
                for (int f = first; f <= last; f++)
                {
                    if ((f - first) % 50 == 0)
                    {
                        float progress = (float)(f - first) / totalFrames;
                        if (EditorUtility.DisplayCancelableProgressBar(
                            "Pulling Per-Frame GC Data",
                            string.Concat("Frame ", (f - first + 1).ToString(), " / ", totalFrames.ToString()),
                            progress))
                            break;
                    }

                    long frameTotal = 0;
                    for (int threadIdx = 0; threadIdx < 64; threadIdx++)
                    {
                        using var raw = ProfilerDriver.GetRawFrameDataView(f, threadIdx);
                        if (!raw.valid) break;

                        int gcAllocId = raw.GetMarkerId("GC.Alloc");
                        if (gcAllocId == FrameDataView.invalidMarkerId) continue;

                        for (int i = 0; i < raw.sampleCount; i++)
                        {
                            if (raw.GetSampleMarkerId(i) != gcAllocId) continue;
                            long bytes = raw.GetSampleMetadataAsLong(i, 0);
                            if (bytes > 0)
                                frameTotal += bytes;
                        }
                    }

                    fullFrameBytes[f - first] = frameTotal;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            m_FrameStore.FullFrameStart = first;
            m_FrameStore.FullFrameEnd = last;
            m_FrameStore.FullFrameBytes = fullFrameBytes;

            m_StartFrameField.value = GCAllocUtils.DisplayFrame(first);
            m_EndFrameField.value = GCAllocUtils.DisplayFrame(last);
            UpdateFrameRangeInfo();

            // Show graph with all bars dimmed (nothing analyzed yet)
            RebuildGraph();

            m_SharedSB.Clear();
            m_SharedSB.Append("Pulled: frames ");
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(first));
            m_SharedSB.Append('–');
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(last));
            m_SharedSB.Append(" (");
            m_SharedSB.Append(totalFrames);
            m_SharedSB.Append(" frames). Click Analyze for full call-stack analysis.");
            m_StatusLabel.text = m_SharedSB.ToString();
        }
```

**Step 3: Verify**

Run: `dotnet build GCAllocAnalyzer.sln` — should compile. Do NOT test in Unity yet (graph controller doesn't read from `m_FrameStore` yet).

**Step 4: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: Pull Data now performs fast per-frame GC scan into GraphFrameStore"
```

---

## Task 4: Wire Controller to Use GraphFrameStore + Dimming

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/PerFrameGraphController.cs`
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Goal:** The graph controller reads bar data from `GraphFrameStore.FullFrameBytes` (always full range). It receives the analyzed range separately and passes it to `GraphElement.SetAnalyzedRange()` for dimming. The window passes both `GraphFrameStore` and the analyzed range on every data update.

**Step 1: Add GraphFrameStore reference and analyzed range fields to controller**

In the `DATA REFERENCES` section of `PerFrameGraphController.cs` (after `m_FilteredGroups`), add:

```csharp
        GraphFrameStore m_FrameStore;
        int m_AnalyzedFrameStart = -1;
        int m_AnalyzedFrameEnd = -1;
```

**Step 2: Modify SetData to accept GraphFrameStore and analyzed range**

Replace the existing `SetData` method with:

```csharp
        /// <summary>
        /// Store data references after each analysis run or Pull Data.
        /// </summary>
        public void SetData(GraphFrameStore frameStore, AnalysisSnapshot snapshot, List<CallsiteGroup> filteredGroups)
        {
            m_FrameStore = frameStore;
            m_Snapshot = snapshot;
            m_FilteredGroups = filteredGroups;

            // Determine analyzed range (if snapshot has data)
            if (snapshot != null && snapshot.HasData)
            {
                m_AnalyzedFrameStart = snapshot.FrameStart;
                m_AnalyzedFrameEnd = snapshot.FrameEnd;
            }
            else
            {
                m_AnalyzedFrameStart = -1;
                m_AnalyzedFrameEnd = -1;
            }
        }
```

**Step 3: Modify RebuildGraph to use FullFrameBytes**

Replace the top of `RebuildGraph()` (from the null check through filling buckets) so it reads from `m_FrameStore.FullFrameBytes` instead of `m_Snapshot.PerFrameBytes`:

```csharp
        public void RebuildGraph()
        {
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData)
            {
                m_GraphFoldout.style.display = DisplayStyle.None;
                return;
            }

            var perFrame = m_FrameStore.FullFrameBytes;
            int frameCount = perFrame.Length;

            m_GraphFoldout.style.display = DisplayStyle.Flex;

            float areaWidth = m_GraphElement.contentRect.width;
            if (float.IsNaN(areaWidth) || areaWidth < 1f) areaWidth = 400f;

            // ── Bucketing ──
            m_FramesPerBucket = 1;
            if (frameCount > (int)areaWidth)
                m_FramesPerBucket = Mathf.CeilToInt((float)frameCount / Mathf.Max(1f, areaWidth));

            int bucketCount = Mathf.CeilToInt((float)frameCount / m_FramesPerBucket);

            // ── Ensure bar buffer capacity ──
            EnsureBarCapacity(ref m_Bars, bucketCount);
            m_BarCount = bucketCount;

            // ── Fill buckets and find max ──
            long maxValue = 0;
            for (int b = 0; b < bucketCount; b++)
            {
                int startIdx = b * m_FramesPerBucket;
                int endIdx = Mathf.Min(startIdx + m_FramesPerBucket, frameCount);
                long bucketMax = 0;
                for (int i = startIdx; i < endIdx; i++)
                {
                    if (perFrame[i] > bucketMax) bucketMax = perFrame[i];
                }

                int frameStart = m_FrameStore.FullFrameStart + startIdx;
                int frameEnd = m_FrameStore.FullFrameStart + endIdx - 1;

                m_Bars[b] = new BarData
                {
                    Value = bucketMax,
                    StartFrame = frameStart,
                    EndFrame = frameEnd
                };

                if (bucketMax > maxValue) maxValue = bucketMax;
            }

            m_YAxisMax = maxValue;

            // ── Compute X/W/Height for each bar ──
            float barWidth = areaWidth / bucketCount;
            for (int b = 0; b < bucketCount; b++)
            {
                m_Bars[b].X = b * barWidth;
                m_Bars[b].W = Mathf.Max(barWidth, 1f);
                m_Bars[b].Height = maxValue > 0
                    ? (float)m_Bars[b].Value / maxValue * k_GraphHeight
                    : 0f;
            }

            // ── Sorted view (order by magnitude) ──
            if (m_OrderByMagnitude)
                ApplySortedOrder(barWidth);
            else
                m_SortedBarIndices = null;

            // ── Grid lines ──
            ComputeGridLines(maxValue, k_GraphHeight);

            // ── Compute analyzed bar range for dimming ──
            int analyzedStartBar = -1, analyzedEndBar = -1;
            if (m_AnalyzedFrameStart >= 0 && m_AnalyzedFrameEnd >= 0)
            {
                ComputeAnalyzedBarRange(out analyzedStartBar, out analyzedEndBar);
            }

            // ── Push data to GraphElement ──
            m_GraphElement.YAxisMax = maxValue;
            m_GraphElement.SetBarData(m_Bars, m_BarCount);
            m_GraphElement.SetGridLines(m_GridLines, m_GridLineCount);
            m_GraphElement.SetAnalyzedRange(analyzedStartBar, analyzedEndBar);
            m_GraphElement.HasData = true;

            // ── Update axis labels ──
            m_GraphYMax.text = GCAllocUtils.FormatBytes(maxValue);
            m_GraphYMid.text = GCAllocUtils.FormatBytes(maxValue / 2);
            m_GraphXStart.text = GCAllocUtils.DisplayFrame(m_FrameStore.FullFrameStart).ToString();
            m_GraphXEnd.text = GCAllocUtils.DisplayFrame(m_FrameStore.FullFrameEnd).ToString();

            // ── Position grid line labels ──
            PositionGridLineLabels();

            // ── Reset tooltip cache (stale after rebuild) ──
            m_LastTooltipBar = -1;

            // ── Clear overlay bar count (stale data) ──
            m_OverlayBarCount = 0;
            m_GraphElement.SetOverlayData(m_OverlayBars, 0);

            // ── Re-apply overlay for currently selected marker ──
            int selectedIdx = m_GetSelectedMarkerIndex != null ? m_GetSelectedMarkerIndex() : -1;
            if (m_FilteredGroups != null &&
                selectedIdx >= 0 &&
                selectedIdx < m_FilteredGroups.Count)
                UpdateOverlay(m_FilteredGroups[selectedIdx]);
            else
                m_GraphOverlayLabel.text = "";
        }
```

**Step 4: Add ComputeAnalyzedBarRange helper**

Add this helper method in the `PRIVATE HELPERS` section of the controller:

```csharp
        void ComputeAnalyzedBarRange(out int startBar, out int endBar)
        {
            startBar = -1;
            endBar = -1;
            if (m_FrameStore == null || m_BarCount == 0) return;
            if (m_AnalyzedFrameStart < 0 || m_AnalyzedFrameEnd < 0) return;

            // Find the first and last bars that overlap with the analyzed frame range
            for (int i = 0; i < m_BarCount; i++)
            {
                // In sorted mode, resolve display index to original bucket
                int origIdx = m_SortedBarIndices != null ? m_SortedBarIndices[i] : i;
                int barFrameStart = m_FrameStore.FullFrameStart + origIdx * m_FramesPerBucket;
                int barFrameEnd = m_FrameStore.FullFrameStart + Mathf.Min((origIdx + 1) * m_FramesPerBucket, m_FrameStore.FullFrameBytes.Length) - 1;

                bool overlaps = barFrameStart <= m_AnalyzedFrameEnd && barFrameEnd >= m_AnalyzedFrameStart;
                if (overlaps)
                {
                    if (startBar < 0) startBar = i;
                    endBar = i;
                }
            }

            // In sorted mode, analyzed bars may not be contiguous. For dimming,
            // we mark individual bars. Change GraphElement to support a callback
            // or bit array if needed. For now, contiguous range works for unsorted.
            // In sorted mode, all bars that had any data in the analyzed range
            // will be scattered — we handle this by not dimming in sorted mode
            // when the analyzed range equals the full range, or by marking all
            // analyzed bars. For simplicity, in sorted mode we set the full range
            // as analyzed (dimming still works correctly for the unsorted case).
            if (m_SortedBarIndices != null)
            {
                // In sorted mode, use a different approach: check if analyzed == full
                if (m_AnalyzedFrameStart == m_FrameStore.FullFrameStart &&
                    m_AnalyzedFrameEnd == m_FrameStore.FullFrameEnd)
                {
                    startBar = 0;
                    endBar = m_BarCount - 1;
                }
                else
                {
                    // Sub-range in sorted mode: all bars in analyzed range are scattered.
                    // We need per-bar dimming. Use a bitfield approach in GraphElement.
                    // For now, mark all as analyzed — will refine in Task 12.
                    startBar = 0;
                    endBar = m_BarCount - 1;
                }
            }
        }
```

**Step 5: Update OnGraphGeometryChanged**

Update the null check in `OnGraphGeometryChanged` to use `m_FrameStore`:

```csharp
        void OnGraphGeometryChanged(GeometryChangedEvent evt)
        {
            if (m_FrameStore == null || !m_FrameStore.HasFullFrameData) return;
            float oldW = evt.oldRect.width;
            float newW = evt.newRect.width;
            if (Mathf.Abs(newW - oldW) < 2f) return;
            RebuildGraph();
        }
```

**Step 6: Update window's RebuildGraph and BuildPerFrameGraph**

In `GCAllocAnalyzerWindow.cs`, update the `RebuildGraph` helper:

```csharp
        void RebuildGraph()
        {
            m_GraphController?.SetData(m_FrameStore, m_Snapshot, m_FilteredGroups);
            m_GraphController?.RebuildGraph();
        }
```

Update `BuildPerFrameGraph()` to pass `m_FrameStore`:

```csharp
        VisualElement BuildPerFrameGraph()
        {
            m_GraphController = new PerFrameGraphController(OnGraphFrameSelected, () => m_MarkerListView?.selectedIndex ?? -1);
            m_GraphController.OnDragCompleted += OnGraphDragCompleted;
            m_GraphController.OnAnalyzeRequested += OnAnalyze;
            m_GraphController.SetData(m_FrameStore, m_Snapshot, m_FilteredGroups);
            return m_GraphController.Root;
        }
```

**Step 7: Update overlay to use FrameStore for frame offsets**

In `PerFrameGraphController.UpdateOverlay()`, the per-frame buffer index calculation needs to use the analyzed range (snapshot), not the full range. The overlay should only apply to analyzed bars. Update the frame count and offset:

```csharp
        public void UpdateOverlay(CallsiteGroup group)
        {
            if (group == null)
            {
                ClearOverlay();
                return;
            }

            if (m_Snapshot == null || m_Snapshot.PerFrameBytes == null) return;
            if (m_BarCount == 0) return;
            if (m_FrameStore == null) return;

            int snapshotFrameCount = m_Snapshot.PerFrameBytes.Length;

            // ── Build per-frame bytes for this group (within analyzed range) ──
            EnsurePerFrameBuffer(snapshotFrameCount);
            for (int i = 0; i < group.Allocations.Count; i++)
            {
                var alloc = group.Allocations[i];
                int idx = alloc.FrameIndex - m_Snapshot.FrameStart;
                if (idx >= 0 && idx < snapshotFrameCount)
                    m_PerFrameBuffer[idx] += alloc.Bytes;
            }

            // ── Bucket the group's per-frame data over the full range ──
            int fullFrameCount = m_FrameStore.FullFrameBytes.Length;
            EnsureBarCapacity(ref m_OverlayBars, m_BarCount);
            m_OverlayBarCount = m_BarCount;

            for (int b = 0; b < m_BarCount; b++)
            {
                int origBucket = m_SortedBarIndices != null ? m_SortedBarIndices[b] : b;
                int startIdx = origBucket * m_FramesPerBucket;
                int endIdx = Mathf.Min(startIdx + m_FramesPerBucket, fullFrameCount);

                long bucketMax = 0;
                for (int i = startIdx; i < endIdx; i++)
                {
                    // Map full-range index to snapshot-relative index
                    int snapshotIdx = (m_FrameStore.FullFrameStart + i) - m_Snapshot.FrameStart;
                    if (snapshotIdx >= 0 && snapshotIdx < snapshotFrameCount)
                    {
                        if (m_PerFrameBuffer[snapshotIdx] > bucketMax)
                            bucketMax = m_PerFrameBuffer[snapshotIdx];
                    }
                }

                m_OverlayBars[b] = new BarData
                {
                    X = m_Bars[b].X,
                    W = m_Bars[b].W,
                    Height = m_YAxisMax > 0
                        ? (float)bucketMax / m_YAxisMax * k_GraphHeight
                        : 0f,
                    StartFrame = m_Bars[b].StartFrame,
                    EndFrame = m_Bars[b].EndFrame,
                    Value = bucketMax
                };
            }

            // ── Push overlay to GraphElement ──
            m_GraphElement.SetOverlayData(m_OverlayBars, m_OverlayBarCount);

            // ── Update overlay label ──
            m_GraphOverlayLabel.text = group.DisplayName;
        }
```

**Step 8: Verify**

Run: `dotnet build GCAllocAnalyzer.sln` — should compile with zero errors.

**Manual test in Unity:**
1. Enter Play Mode, run 100+ frames
2. Open Profiler, enable GC.Alloc call stacks
3. Open GC Alloc Analyzer window
4. Click Pull Data → graph should appear with ALL bars dimmed (30% opacity)
5. Click Analyze → bars within analyzed range should brighten to full opacity

**Step 9: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/PerFrameGraphController.cs Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: wire graph controller to use GraphFrameStore + analyzed-range dimming"
```

---

## Task 5: Cache Analysis Results in GraphFrameStore

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Goal:** After `RunAnalysis` completes, cache the extracted `RawAllocations` and thread names into `GraphFrameStore` so subsequent sub-range analyses can filter from cache without Profiler access.

**Step 1: Add caching call at end of RunAnalysis**

In `RunAnalysis()`, after the line `ComputeSnapshotPerFrameBytes();` (around line 1212), add:

```csharp
            // Cache full extraction for instant sub-range analysis
            m_FrameStore.CacheAnalysis(m_Snapshot.RawAllocations, m_Snapshot.SortedThreadNames);
```

**Step 2: Ensure Pull Data populates FullFrameBytes when analyzing without Pull Data first**

In `RunAnalysis()`, after setting `m_Snapshot.FrameEnd` (around line 1198), add a fallback that populates `m_FrameStore` from the snapshot's per-frame data if Pull Data wasn't called first:

```csharp
            // If user clicked Analyze without Pull Data, populate FrameStore from snapshot
            if (!m_FrameStore.HasFullFrameData
                || m_FrameStore.FullFrameStart != startFrame
                || m_FrameStore.FullFrameEnd != endFrame)
            {
                m_FrameStore.FullFrameStart = startFrame;
                m_FrameStore.FullFrameEnd = endFrame;
                // Will be filled after ComputeSnapshotPerFrameBytes
            }
```

Then after `ComputeSnapshotPerFrameBytes();`, if the frame store was just created from the snapshot, copy the data:

```csharp
            // If FrameStore was created from snapshot, copy per-frame bytes
            if (m_FrameStore.FullFrameBytes == null
                || m_FrameStore.FullFrameBytes.Length != m_Snapshot.PerFrameBytes.Length)
            {
                int count = m_Snapshot.PerFrameBytes.Length;
                m_FrameStore.FullFrameBytes = new long[count];
                Array.Copy(m_Snapshot.PerFrameBytes, m_FrameStore.FullFrameBytes, count);
            }
```

**Step 3: Clear cache on new Pull Data (already done in Task 3)**

The `OnPullData` method from Task 3 already calls `m_FrameStore.Clear()`.

**Step 4: Verify**

Run: `dotnet build GCAllocAnalyzer.sln` — should compile.

**Step 5: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: cache analysis results in GraphFrameStore after RunAnalysis"
```

---

## Task 6: Implement RebuildFromCache

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Goal:** New method `RebuildFromCache(int startFrame, int endFrame)` that filters `GraphFrameStore.CachedRawAllocations` by frame range, rebuilds groupings/stats/UI, and updates the graph — all without Profiler access. Near-instant.

**Step 1: Add RebuildFromCache method**

Add this new method after `RunAnalysis` (in the `DATA EXTRACTION` section):

```csharp
        /// <summary>
        /// Rebuild the analysis view from cached data for a sub-range.
        /// Filters CachedRawAllocations by frame range and rebuilds all
        /// groupings, stats, and UI. No Profiler access — near-instant.
        /// </summary>
        void RebuildFromCache(int startFrame, int endFrame)
        {
            if (!m_FrameStore.HasCachedAnalysis)
            {
                m_StatusLabel.text = "No cached analysis. Run Analyze first.";
                return;
            }

            m_Snapshot.RawAllocations.Clear();
            m_AllThreadNames.Clear();
            m_SelectedThreads.Clear();
            long totalBytes = 0;
            bool anyCallStacks = false;

            // Filter cached allocations by frame range
            var cached = m_FrameStore.CachedRawAllocations;
            for (int i = 0; i < cached.Count; i++)
            {
                var alloc = cached[i];
                if (alloc.FrameIndex < startFrame || alloc.FrameIndex > endFrame)
                    continue;

                m_Snapshot.RawAllocations.Add(alloc);
                totalBytes += alloc.Bytes;
                m_AllThreadNames.Add(alloc.ThreadDisplayName);
                if (alloc.ResolvedCallStack != null && alloc.ResolvedCallStack.Count > 0)
                    anyCallStacks = true;
            }

            m_Snapshot.TotalBytes = totalBytes;
            m_Snapshot.TotalCount = m_Snapshot.RawAllocations.Count;
            m_Snapshot.FrameStart = startFrame;
            m_Snapshot.FrameEnd = endFrame;
            m_Snapshot.HadCallStacks = anyCallStacks;

            // Rebuild thread names
            m_Snapshot.SortedThreadNames.Clear();
            foreach (string t in m_AllThreadNames)
                m_Snapshot.SortedThreadNames.Add(t);
            m_Snapshot.SortedThreadNames.Sort(StringComparer.Ordinal);
            UpdateThreadButtonLabel();

            // Rebuild groupings and stats
            BuildGrouping(true, m_Snapshot.GroupsByFullCallstack);
            BuildGrouping(false, m_Snapshot.GroupsByTopFrame);
            ComputeSnapshotPerFrameBytes();

            m_ActiveGroups = m_GroupByCallsite.value
                ? m_Snapshot.GroupsByFullCallstack : m_Snapshot.GroupsByTopFrame;
            BuildThreadIndex(m_ActiveGroups);
            BuildTopOffenders();
            ApplyFilters();
            UpdateDataSummary();
            ShowNoDataState(m_ActiveGroups.Count == 0);
            RebuildGraph();
            m_SaveBtn?.SetEnabled(m_Snapshot.HasData);
            m_ExportBtn?.SetEnabled(m_Snapshot.HasData);

            // Update frame range fields
            m_StartFrameField.SetValueWithoutNotify(GCAllocUtils.DisplayFrame(startFrame));
            m_EndFrameField.SetValueWithoutNotify(GCAllocUtils.DisplayFrame(endFrame));
            UpdateFrameRangeInfo();

            // Status
            int totalFrames = endFrame - startFrame + 1;
            m_SharedSB.Clear();
            m_SharedSB.Append("Sub-range: ");
            m_SharedSB.Append(totalFrames);
            m_SharedSB.Append(" frames (");
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(startFrame));
            m_SharedSB.Append('–');
            m_SharedSB.Append(GCAllocUtils.DisplayFrame(endFrame));
            m_SharedSB.Append(") | ");
            m_SharedSB.Append(GCAllocUtils.FormatBytes(totalBytes));
            m_SharedSB.Append(", ");
            m_SharedSB.Append(m_Snapshot.TotalCount);
            m_SharedSB.Append(" allocs (from cache)");
            m_StatusLabel.text = m_SharedSB.ToString();

            if (m_FilteredGroups.Count > 0)
                m_MarkerListView.selectedIndex = 0;
        }
```

**Step 2: Verify**

Run: `dotnet build GCAllocAnalyzer.sln` — should compile. Method is not called yet.

**Step 3: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: add RebuildFromCache for instant sub-range analysis"
```

---

## Task 7: Wire Drag-Complete to Auto-Analyze from Cache

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Goal:** When the user drag-selects a range on the graph and releases, the tool auto-analyzes from cache (instant) instead of just updating the frame fields. Selection clears after analysis (the drag "became" the analyzed range).

**Step 1: Update OnGraphDragCompleted**

Replace the existing `OnGraphDragCompleted` method with:

```csharp
        void OnGraphDragCompleted(int startFrame, int endFrame)
        {
            if (m_FrameStore.HasCachedAnalysis)
            {
                // Auto-analyze from cache — instant
                RebuildFromCache(startFrame, endFrame);

                // Clear visual selection (the selection "became" the analyzed range)
                m_GraphController?.ClearSelection();
            }
            else
            {
                // No cache yet — just update frame fields
                if (m_StartFrameField != null)
                    m_StartFrameField.SetValueWithoutNotify(GCAllocUtils.DisplayFrame(startFrame));
                if (m_EndFrameField != null)
                    m_EndFrameField.SetValueWithoutNotify(GCAllocUtils.DisplayFrame(endFrame));
                UpdateFrameRangeInfo();
            }
        }
```

**Step 2: Add ClearSelection to controller**

In `PerFrameGraphController.cs`, add a public method:

```csharp
        /// <summary>
        /// Clear the visual selection and highlighted bar.
        /// </summary>
        public void ClearSelection()
        {
            m_GraphElement.SetSelection(-1, -1);
            m_GraphElement.SetHighlightedBar(-1);
            m_LastSelectionStartBar = -1;
            m_LastSelectionEndBar = -1;
        }
```

**Step 3: Verify**

Run: `dotnet build GCAllocAnalyzer.sln` — should compile.

**Manual test in Unity:**
1. Pull Data → Analyze → graph shows bright bars
2. Drag-select a sub-range → table instantly updates with sub-range data, dimming appears on un-analyzed bars, selection highlight clears

**Step 4: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs Assets/GCAllocAnalyzer/Editor/PerFrameGraphController.cs
git commit -m "feat: auto-analyze from cache on drag-complete"
```

---

## Task 8: Add Inline Reset Button

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/PerFrameGraphController.cs`
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Goal:** A small "Reset" button appears in the graph area corner when viewing a sub-range. Clicking it restores the full cached snapshot instantly.

**Step 1: Add reset button UI to controller**

In the `UI ELEMENTS` section of `PerFrameGraphController.cs`, add:

```csharp
        Button m_ResetBtn;
```

Add a new event:

```csharp
        /// <summary>Invoked when the user clicks the Reset button to restore full range.</summary>
        public event Action OnResetRequested;
```

**Step 2: Build the reset button in BuildUI**

In `BuildUI()`, after adding `m_GraphElement` to `chartColumn` and before the X-axis section, add:

```csharp
            // ── Reset button (inline, only visible during sub-range) ──
            m_ResetBtn = new Button(() => OnResetRequested?.Invoke())
            {
                text = "Reset to Full Range",
                tooltip = "Restore full-range analysis from cache (instant)",
                style =
                {
                    fontSize = 10,
                    paddingLeft = 6,
                    paddingRight = 6,
                    paddingTop = 2,
                    paddingBottom = 2,
                    position = Position.Absolute,
                    top = 4,
                    right = 4,
                    display = DisplayStyle.None
                }
            };
            m_GraphElement.Add(m_ResetBtn);
```

**Step 3: Update reset button visibility in RebuildGraph**

At the end of `RebuildGraph()`, after the overlay logic, add:

```csharp
            // ── Show/hide reset button ──
            UpdateResetButtonVisibility();
```

Add the helper:

```csharp
        void UpdateResetButtonVisibility()
        {
            if (m_ResetBtn == null || m_FrameStore == null) return;

            bool isSubRange = m_AnalyzedFrameStart >= 0
                && m_AnalyzedFrameEnd >= 0
                && m_FrameStore.HasCachedAnalysis
                && (m_AnalyzedFrameStart != m_FrameStore.FullFrameStart
                    || m_AnalyzedFrameEnd != m_FrameStore.FullFrameEnd);

            m_ResetBtn.style.display = isSubRange ? DisplayStyle.Flex : DisplayStyle.None;
        }
```

**Step 4: Wire reset in the window**

In `GCAllocAnalyzerWindow.BuildPerFrameGraph()`, subscribe to the reset event:

```csharp
            m_GraphController.OnResetRequested += OnGraphResetRequested;
```

Add the handler:

```csharp
        void OnGraphResetRequested()
        {
            if (!m_FrameStore.HasCachedAnalysis) return;
            RebuildFromCache(m_FrameStore.FullFrameStart, m_FrameStore.FullFrameEnd);
            m_GraphController?.ClearSelection();
        }
```

**Step 5: Verify**

Run: `dotnet build GCAllocAnalyzer.sln` — should compile.

**Manual test:**
1. Pull Data → Analyze → no Reset button visible
2. Drag-select sub-range → Reset button appears in top-right corner of graph
3. Click Reset → table restores to full range, Reset button disappears

**Step 6: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/PerFrameGraphController.cs Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: add inline Reset button for restoring full-range analysis"
```

---

## Task 9: Add Viewport State + WASD Zoom/Pan

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/PerFrameGraphController.cs`
- Modify: `Assets/GCAllocAnalyzer/Editor/GraphElement.cs`

**Goal:** The detail view supports zoom/pan via WASD keys and mouse wheel. A viewport state (`m_ViewportStart`, `m_ViewportEnd`) controls which bars are visible. Only visible bars are drawn (performance). Bar width increases when zoomed in.

**Step 1: Add viewport state to controller**

In the `BAR COMPUTATION BUFFERS` section, add:

```csharp
        // Viewport state: normalized range [0,1] over the full bar set
        float m_ViewportStart;
        float m_ViewportEnd = 1f;
        const float k_MinVisibleBars = 10f;
```

**Step 2: Add viewport methods to controller**

Add a new section after `SORTING`:

```csharp
        // ═══════════════════════════════════════════════════
        //  VIEWPORT — zoom / pan
        // ═══════════════════════════════════════════════════

        /// <summary>Current viewport normalized start [0,1].</summary>
        public float ViewportStart => m_ViewportStart;

        /// <summary>Current viewport normalized end [0,1].</summary>
        public float ViewportEnd => m_ViewportEnd;

        /// <summary>True when the detail view is zoomed in.</summary>
        public bool IsZoomedIn => m_ViewportEnd - m_ViewportStart < 0.999f;

        public void ResetViewport()
        {
            m_ViewportStart = 0f;
            m_ViewportEnd = 1f;
            RebuildGraph();
        }

        void ZoomViewport(float zoomFactor, float anchorNormalized)
        {
            float span = m_ViewportEnd - m_ViewportStart;
            float newSpan = Mathf.Clamp(span * zoomFactor, k_MinVisibleBars / Mathf.Max(1, m_BarCount), 1f);
            float anchor = m_ViewportStart + span * anchorNormalized;

            m_ViewportStart = anchor - newSpan * anchorNormalized;
            m_ViewportEnd = m_ViewportStart + newSpan;
            ClampViewport();
            RebuildGraph();
        }

        void PanViewport(float delta)
        {
            float span = m_ViewportEnd - m_ViewportStart;
            m_ViewportStart += delta;
            m_ViewportEnd = m_ViewportStart + span;
            ClampViewport();
            RebuildGraph();
        }

        void ClampViewport()
        {
            float span = m_ViewportEnd - m_ViewportStart;
            if (m_ViewportStart < 0f)
            {
                m_ViewportStart = 0f;
                m_ViewportEnd = span;
            }
            if (m_ViewportEnd > 1f)
            {
                m_ViewportEnd = 1f;
                m_ViewportStart = 1f - span;
            }
            if (m_ViewportStart < 0f) m_ViewportStart = 0f;
        }
```

**Step 3: Add WASD + mouse wheel handlers**

Register new keyboard and wheel handlers in `BuildUI()`, after the existing `m_GraphElement` callbacks:

```csharp
            m_GraphElement.RegisterCallback<KeyDownEvent>(OnGraphKeyDown);
            m_GraphElement.RegisterCallback<WheelEvent>(OnGraphWheel);
```

Add the handlers:

```csharp
        void OnGraphKeyDown(KeyDownEvent evt)
        {
            if (m_BarCount == 0) return;
            bool handled = false;
            float span = m_ViewportEnd - m_ViewportStart;
            float panStep = span * 0.15f;

            switch (evt.keyCode)
            {
                case KeyCode.W:
                    ZoomViewport(0.7f, 0.5f); // zoom in centered
                    handled = true;
                    break;
                case KeyCode.S:
                    ZoomViewport(1.4f, 0.5f); // zoom out centered
                    handled = true;
                    break;
                case KeyCode.A:
                    PanViewport(-panStep);
                    handled = true;
                    break;
                case KeyCode.D:
                    PanViewport(panStep);
                    handled = true;
                    break;
            }

            if (handled)
                evt.StopPropagation();
        }

        void OnGraphWheel(WheelEvent evt)
        {
            if (m_BarCount == 0) return;

            // Compute anchor as normalized position within the graph
            float localX = evt.localMousePosition.x;
            float areaWidth = m_GraphElement.contentRect.width;
            float anchor = areaWidth > 0 ? Mathf.Clamp01(localX / areaWidth) : 0.5f;

            float zoomFactor = evt.delta.y > 0 ? 1.2f : 0.8f;
            ZoomViewport(zoomFactor, anchor);
            evt.StopPropagation();
        }
```

**Step 4: Modify RebuildGraph to only draw visible bars**

In `RebuildGraph()`, after computing all bars and applying sort, add viewport clipping. Replace the section that pushes data to `GraphElement`:

After the existing `// ── Compute X/W/Height for each bar ──` block and sort, add viewport-aware layout:

```csharp
            // ── Viewport clipping — only layout visible bars ──
            int visibleStart = Mathf.FloorToInt(m_ViewportStart * bucketCount);
            int visibleEnd = Mathf.CeilToInt(m_ViewportEnd * bucketCount);
            visibleStart = Mathf.Clamp(visibleStart, 0, bucketCount - 1);
            visibleEnd = Mathf.Clamp(visibleEnd, visibleStart, bucketCount);
            int visibleCount = visibleEnd - visibleStart;

            if (visibleCount <= 0)
            {
                m_GraphElement.SetBarData(m_Bars, 0);
                return;
            }

            // Recompute X/W for visible bars to fill the graph width
            float visibleBarWidth = areaWidth / visibleCount;
            for (int i = 0; i < visibleCount; i++)
            {
                int srcIdx = visibleStart + i;
                m_Bars[i] = m_Bars[srcIdx]; // shift to front of array
                m_Bars[i].X = i * visibleBarWidth;
                m_Bars[i].W = Mathf.Max(visibleBarWidth, 1f);
                // Recalculate height against global max (unchanged)
                m_Bars[i].Height = maxValue > 0
                    ? (float)m_Bars[i].Value / maxValue * k_GraphHeight
                    : 0f;
            }
            m_BarCount = visibleCount;
```

**Note:** This is a significant restructure of `RebuildGraph`. The implementer should be careful to:
- Apply sorting BEFORE viewport clipping
- Maintain `m_SortedBarIndices` mapping through the viewport offset
- Store `m_ViewportStartBucket = visibleStart` as a field for hit-testing offset

Add this field to the `BAR COMPUTATION BUFFERS` section:

```csharp
        int m_ViewportStartBucket; // first bucket index visible in the viewport
        int m_TotalBucketCount;    // total buckets before viewport clipping
```

**Step 5: Update hit-testing to account for viewport offset**

Update `MapBarRangeToFrameRange` to use the viewport offset when resolving frames:

In `OnBarClicked`, the `resolvedIndex` needs to account for viewport offset:

```csharp
            int resolvedIndex = m_SortedBarIndices != null
                ? m_SortedBarIndices[barIndex + m_ViewportStartBucket]
                : barIndex + m_ViewportStartBucket;
```

**Step 6: Verify**

Run: `dotnet build GCAllocAnalyzer.sln` — should compile.

**Manual test:**
1. Pull Data → Analyze → graph shows bars
2. Press W → graph zooms in (fewer bars, wider)
3. Press S → graph zooms out
4. Press A/D → graph pans left/right
5. Mouse wheel over graph → zooms centered on cursor

**Step 7: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/PerFrameGraphController.cs Assets/GCAllocAnalyzer/Editor/GraphElement.cs
git commit -m "feat: add WASD zoom/pan and mouse wheel zoom to graph"
```

---

## Task 10: Add Overview Strip

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/PerFrameGraphController.cs`

**Goal:** A miniature overview strip (~24px tall) above the detail view shows the entire capture range. A semi-transparent viewport rectangle indicates the currently visible portion. Drag the rectangle to pan, click outside to jump.

**Step 1: Add overview constants and fields**

```csharp
        const float k_OverviewHeight = 24f;
        GraphElement m_OverviewElement;
        VisualElement m_ViewportRect;
        BarData[] m_OverviewBars;
        int m_OverviewBarCount;
        bool m_OverviewDragging;
        float m_OverviewDragStartX;
        float m_OverviewDragStartVP;
```

**Step 2: Build the overview strip in BuildUI**

In `BuildUI()`, add the overview strip into `chartColumn` BEFORE `m_GraphElement`:

```csharp
            // ── Overview strip (miniature full-range view) ──
            var overviewContainer = new VisualElement
            {
                style =
                {
                    height = k_OverviewHeight,
                    marginBottom = 2,
                    flexShrink = 0
                }
            };

            m_OverviewElement = new GraphElement
            {
                style =
                {
                    height = k_OverviewHeight,
                    flexGrow = 1
                }
            };
            m_OverviewElement.pickingMode = PickingMode.Ignore;

            m_ViewportRect = new VisualElement
            {
                style =
                {
                    position = Position.Absolute,
                    top = 0,
                    bottom = 0,
                    backgroundColor = new Color(1f, 1f, 1f, 0.15f),
                    borderLeftWidth = 1, borderRightWidth = 1,
                    borderLeftColor = new Color(1f, 1f, 1f, 0.5f),
                    borderRightColor = new Color(1f, 1f, 1f, 0.5f)
                }
            };

            overviewContainer.Add(m_OverviewElement);
            overviewContainer.Add(m_ViewportRect);
            overviewContainer.RegisterCallback<PointerDownEvent>(OnOverviewPointerDown);
            overviewContainer.RegisterCallback<PointerMoveEvent>(OnOverviewPointerMove);
            overviewContainer.RegisterCallback<PointerUpEvent>(OnOverviewPointerUp);

            chartColumn.Add(overviewContainer);
```

**Step 3: Build overview bars in RebuildGraph**

At the end of `RebuildGraph()` (before the overlay section), add:

```csharp
            // ── Overview strip bars (always full range, no viewport clipping) ──
            RebuildOverviewStrip(perFrame, frameCount, maxValue);
            UpdateViewportRect();
```

Add the helper:

```csharp
        void RebuildOverviewStrip(long[] perFrame, int frameCount, long maxValue)
        {
            if (m_OverviewElement == null) return;

            float overviewWidth = m_OverviewElement.contentRect.width;
            if (float.IsNaN(overviewWidth) || overviewWidth < 1f) overviewWidth = 400f;

            int overviewBucketSize = 1;
            if (frameCount > (int)overviewWidth)
                overviewBucketSize = Mathf.CeilToInt((float)frameCount / Mathf.Max(1f, overviewWidth));

            int bucketCount = Mathf.CeilToInt((float)frameCount / overviewBucketSize);
            EnsureBarCapacity(ref m_OverviewBars, bucketCount);
            m_OverviewBarCount = bucketCount;

            float barWidth = overviewWidth / bucketCount;
            for (int b = 0; b < bucketCount; b++)
            {
                int startIdx = b * overviewBucketSize;
                int endIdx = Mathf.Min(startIdx + overviewBucketSize, frameCount);
                long bucketMax = 0;
                for (int i = startIdx; i < endIdx; i++)
                    if (perFrame[i] > bucketMax) bucketMax = perFrame[i];

                m_OverviewBars[b] = new BarData
                {
                    X = b * barWidth,
                    W = Mathf.Max(barWidth, 1f),
                    Height = maxValue > 0 ? (float)bucketMax / maxValue * k_OverviewHeight : 0f,
                    Value = bucketMax,
                    StartFrame = m_FrameStore.FullFrameStart + startIdx,
                    EndFrame = m_FrameStore.FullFrameStart + endIdx - 1
                };
            }

            m_OverviewElement.YAxisMax = maxValue;
            m_OverviewElement.SetBarData(m_OverviewBars, m_OverviewBarCount);

            // Apply analyzed-range dimming to overview too
            // (reuse the same analyzed range computation)
            if (m_AnalyzedFrameStart >= 0 && m_AnalyzedFrameEnd >= 0)
            {
                int aStart = -1, aEnd = -1;
                for (int i = 0; i < m_OverviewBarCount; i++)
                {
                    if (m_OverviewBars[i].EndFrame >= m_AnalyzedFrameStart &&
                        m_OverviewBars[i].StartFrame <= m_AnalyzedFrameEnd)
                    {
                        if (aStart < 0) aStart = i;
                        aEnd = i;
                    }
                }
                m_OverviewElement.SetAnalyzedRange(aStart, aEnd);
            }
            else
            {
                m_OverviewElement.SetAnalyzedRange(-1, -1);
            }

            m_OverviewElement.HasData = true;
        }

        void UpdateViewportRect()
        {
            if (m_ViewportRect == null || m_OverviewElement == null) return;
            float overviewWidth = m_OverviewElement.contentRect.width;
            if (float.IsNaN(overviewWidth) || overviewWidth < 1f) return;

            m_ViewportRect.style.left = m_ViewportStart * overviewWidth;
            m_ViewportRect.style.width = (m_ViewportEnd - m_ViewportStart) * overviewWidth;

            // Hide overview strip when fully zoomed out
            bool show = IsZoomedIn;
            m_OverviewElement.parent.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }
```

**Step 4: Add overview pointer handlers**

```csharp
        void OnOverviewPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || m_OverviewBarCount == 0) return;

            float overviewWidth = m_OverviewElement.contentRect.width;
            if (overviewWidth < 1f) return;

            float clickNorm = evt.localPosition.x / overviewWidth;
            float span = m_ViewportEnd - m_ViewportStart;

            // Check if clicking within viewport rect → start drag
            if (clickNorm >= m_ViewportStart && clickNorm <= m_ViewportEnd)
            {
                m_OverviewDragging = true;
                m_OverviewDragStartX = evt.localPosition.x;
                m_OverviewDragStartVP = m_ViewportStart;
                ((VisualElement)evt.target).CapturePointer(evt.pointerId);
            }
            else
            {
                // Click outside → jump viewport center to click position
                float newStart = clickNorm - span * 0.5f;
                m_ViewportStart = newStart;
                m_ViewportEnd = newStart + span;
                ClampViewport();
                RebuildGraph();
            }

            evt.StopPropagation();
        }

        void OnOverviewPointerMove(PointerMoveEvent evt)
        {
            if (!m_OverviewDragging) return;

            float overviewWidth = m_OverviewElement.contentRect.width;
            if (overviewWidth < 1f) return;

            float dx = evt.localPosition.x - m_OverviewDragStartX;
            float deltaNorm = dx / overviewWidth;
            float span = m_ViewportEnd - m_ViewportStart;

            m_ViewportStart = m_OverviewDragStartVP + deltaNorm;
            m_ViewportEnd = m_ViewportStart + span;
            ClampViewport();
            RebuildGraph();

            evt.StopPropagation();
        }

        void OnOverviewPointerUp(PointerUpEvent evt)
        {
            if (evt.button != 0) return;
            if (m_OverviewDragging)
            {
                m_OverviewDragging = false;
                ((VisualElement)evt.target).ReleasePointer(evt.pointerId);
            }
            evt.StopPropagation();
        }
```

**Step 5: Verify**

Run: `dotnet build GCAllocAnalyzer.sln` — should compile.

**Manual test:**
1. Pull Data → Analyze → press W to zoom in
2. Overview strip appears above the detail view with a viewport rectangle
3. Drag the viewport rectangle → detail view pans
4. Click outside the rectangle → detail view jumps

**Step 6: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/PerFrameGraphController.cs
git commit -m "feat: add Perfetto-style overview strip with viewport navigation"
```

---

## Task 11: Add Horizontal Scroller

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/PerFrameGraphController.cs`

**Goal:** A horizontal `Scroller` below the detail view, visible only when zoomed in, wired to the viewport state.

**Step 1: Add scroller field and build in UI**

In the `UI ELEMENTS` section, add:

```csharp
        Scroller m_HScroller;
```

In `BuildUI()`, add the scroller into `chartColumn` AFTER `m_GraphElement` and BEFORE the x-axis:

```csharp
            // ── Horizontal scroller (visible when zoomed) ──
            m_HScroller = new Scroller(0, 1, OnScrollerChanged, SliderDirection.Horizontal)
            {
                style =
                {
                    height = 14,
                    display = DisplayStyle.None
                }
            };
            chartColumn.Add(m_HScroller);
```

**Step 2: Wire scroller to viewport**

```csharp
        void OnScrollerChanged(float value)
        {
            float span = m_ViewportEnd - m_ViewportStart;
            m_ViewportStart = value;
            m_ViewportEnd = value + span;
            ClampViewport();
            RebuildGraph();
        }
```

**Step 3: Update scroller in RebuildGraph**

At the end of `RebuildGraph()`, add:

```csharp
            // ── Update horizontal scroller ──
            UpdateHorizontalScroller();
```

```csharp
        void UpdateHorizontalScroller()
        {
            if (m_HScroller == null) return;

            if (!IsZoomedIn)
            {
                m_HScroller.style.display = DisplayStyle.None;
                return;
            }

            m_HScroller.style.display = DisplayStyle.Flex;
            float span = m_ViewportEnd - m_ViewportStart;
            m_HScroller.lowValue = 0;
            m_HScroller.highValue = Mathf.Max(0, 1f - span);
            m_HScroller.slider.pageSize = span;
            m_HScroller.SetValueWithoutNotify(m_ViewportStart);
        }
```

**Step 4: Verify**

Run: `dotnet build GCAllocAnalyzer.sln` — should compile.

**Manual test:**
1. Zoom in with W key → scroller appears below graph
2. Drag scroller → detail view pans
3. Zoom out fully → scroller disappears

**Step 5: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/PerFrameGraphController.cs
git commit -m "feat: add horizontal scroller for zoomed graph navigation"
```

---

## Task 12: Integration Polish

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/PerFrameGraphController.cs`
- Modify: `Assets/GCAllocAnalyzer/Editor/GraphElement.cs`
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs`

**Goal:** Fix edge cases, polish interactions, and ensure all features work together.

**Step 1: Update context menu for new workflow**

In `OnGraphContextMenu`, update "Analyze Selection" to use `RebuildFromCache` via the existing event:

```csharp
            evt.menu.AppendAction("Analyze Selection", _ =>
            {
                if (!hasActiveSelection) return;
                MapBarRangeToFrameRange(selStart, selEnd, out int rangeStart, out int rangeEnd);
                if (OnDragCompleted != null)
                    OnDragCompleted(rangeStart, rangeEnd);
            },
            hasActiveSelection ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
```

Remove the separate `OnAnalyzeRequested` invocation from the "Analyze Selection" action (it now uses the same drag-complete path).

**Step 2: Add "Reset Zoom" to context menu**

```csharp
            evt.menu.AppendAction("Reset Zoom", _ => ResetViewport(),
                IsZoomedIn ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
```

**Step 3: Handle Enter key to analyze keyboard selection**

In the existing keyboard handler for `GraphElement`, add Enter key handling. In `OnKeyDown` in `GraphElement.cs`, add a case:

```csharp
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (m_SelectionStart >= 0 && m_SelectionEnd >= 0)
                    {
                        if (DragCompleted != null)
                            DragCompleted(m_SelectionStart, m_SelectionEnd);
                    }
                    handled = true;
                    break;
```

**Step 4: Ensure OnLoadSnapshot still works**

In `OnLoadSnapshot()` (in the window), after loading and restoring the snapshot, also populate `m_FrameStore` from the loaded data so the graph works correctly:

```csharp
            // Populate frame store from loaded snapshot
            m_FrameStore.FullFrameStart = m_Snapshot.FrameStart;
            m_FrameStore.FullFrameEnd = m_Snapshot.FrameEnd;
            if (m_Snapshot.PerFrameBytes != null)
            {
                int count = m_Snapshot.PerFrameBytes.Length;
                m_FrameStore.FullFrameBytes = new long[count];
                Array.Copy(m_Snapshot.PerFrameBytes, m_FrameStore.FullFrameBytes, count);
            }
            m_FrameStore.CacheAnalysis(m_Snapshot.RawAllocations, m_Snapshot.SortedThreadNames);
```

**Step 5: Ensure TryRestoreAfterReload works with GraphFrameStore**

In `TryRestoreAfterReload()`, after `ComputeSnapshotPerFrameBytes()`, add:

```csharp
            // Restore frame store from snapshot if needed
            if (!m_FrameStore.HasFullFrameData && m_Snapshot.PerFrameBytes != null)
            {
                m_FrameStore.FullFrameStart = m_Snapshot.FrameStart;
                m_FrameStore.FullFrameEnd = m_Snapshot.FrameEnd;
                int count = m_Snapshot.PerFrameBytes.Length;
                m_FrameStore.FullFrameBytes = new long[count];
                Array.Copy(m_Snapshot.PerFrameBytes, m_FrameStore.FullFrameBytes, count);
            }
```

**Step 6: X-axis labels show viewport range when zoomed**

Update X-axis labels in `RebuildGraph()` to show the visible range:

```csharp
            // ── Update axis labels ──
            m_GraphYMax.text = GCAllocUtils.FormatBytes(maxValue);
            m_GraphYMid.text = GCAllocUtils.FormatBytes(maxValue / 2);
            if (m_BarCount > 0)
            {
                m_GraphXStart.text = GCAllocUtils.DisplayFrame(m_Bars[0].StartFrame).ToString();
                m_GraphXEnd.text = GCAllocUtils.DisplayFrame(m_Bars[m_BarCount - 1].EndFrame).ToString();
            }
```

**Step 7: Verify**

Run: `dotnet build GCAllocAnalyzer.sln` — should compile.

**Comprehensive manual test:**
1. Pull Data → graph shows all bars dimmed
2. Analyze → bars brighten in analyzed range
3. Drag-select sub-range → instant re-analysis, dimming updates
4. Click Reset → full range restored
5. WASD zoom/pan → overview strip appears, scroller appears
6. Drag overview viewport → detail pans
7. Context menu → Select All, Analyze Selection, Reset Zoom all work
8. Enter key on keyboard selection → triggers analysis
9. Save/Load snapshot → graph restores correctly
10. Domain reload (edit a script) → graph restores correctly

**Step 8: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/PerFrameGraphController.cs Assets/GCAllocAnalyzer/Editor/GraphElement.cs Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: integration polish — context menus, Enter key, load/restore, zoom labels"
```

---

## Task 13: Final Verification + Squash Commit

**Step 1: Full build verification**

Run: `dotnet build GCAllocAnalyzer.sln` — must compile with zero errors, zero warnings.

**Step 2: Full manual test pass**

Follow the complete test checklist from Task 12 Step 7.

**Step 3: Review all changes**

```bash
git diff develop --stat
git log --oneline develop..HEAD
```

Verify all changes are committed and the branch is clean.

---

## Parallelism Summary for Execution

When using `superpowers:subagent-driven-development` or `superpowers:dispatching-parallel-agents`:

| Phase | Tasks | Can Parallelize? |
|-------|-------|-----------------|
| 1 | Task 1, Task 2 | **Yes** — independent files |
| 2 | Task 3 | Sequential (depends on Task 1) |
| 3 | Task 4 | Sequential (depends on Tasks 1 + 2 + 3) |
| 4 | Task 5 | Sequential (depends on Task 3) |
| 5 | Task 6 | Sequential (depends on Task 5) |
| 6 | Task 7, Task 8 | **Yes** — independent features, both depend on Task 6 |
| 7 | Task 9 | Sequential (depends on Task 4) |
| 8 | Task 10, Task 11 | **Yes** — independent UI, both depend on Task 9 |
| 9 | Task 12 | Sequential (depends on all above) |
| 10 | Task 13 | Sequential (final verification) |
