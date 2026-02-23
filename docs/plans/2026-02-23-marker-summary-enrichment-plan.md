# Task 6: Marker Summary Enrichment — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enrich the right-panel "Selected Allocation Site" section with per-frame statistics, clickable frame links, and top 3 worst frames.

**Architecture:** All per-frame stats already exist on `CallsiteGroup` from Task 3. This task adds a `TopWorstFrames` field computed during grouping, builds new UI containers in `BuildRightPanel()`, populates them in `UpdateMarkerSummary()`, and adds a lightweight `NavigateToFrame()` helper for clickable frame links.

**Tech Stack:** C# / Unity UIElements (VisualElement, Label, ClickEvent)

---

### Task 1: Add TopWorstFrames to CallsiteGroup and compute during grouping

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs:3061-3089` (CallsiteGroup class)
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs:1795-1862` (ComputePerFrameStats)
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs:1746-1757` (formatted string section in BuildGrouping)

**Step 1: Add TopWorstFrames field to CallsiteGroup**

In `CallsiteGroup` (line 3078), after `public int FirstFrame;`, add:

```csharp
            public int[] TopWorstFrameIndices;   // up to 3, descending by bytes
            public long[] TopWorstFrameBytes;     // parallel array, same length
```

Use parallel arrays instead of value tuples — simpler for serialization and avoids allocating tuple objects per group.

After `public string FormattedMean;` (line 3088), add:

```csharp
            public string[] FormattedTopWorst;    // pre-built "frame N — X KB" strings
```

**Step 2: Compute top 3 worst frames in ComputePerFrameStats**

In `ComputePerFrameStats()`, after the existing min/max/sum loop (after line 1835, before `if (framesWithAllocs == 0) return;`), add top-3 tracking. We can extract the top 3 from the buffer in a single O(n) pass since we already iterate it:

```csharp
            // Top 3 worst frames — extract from buffer before we pack it for median
            long top1 = 0, top2 = 0, top3 = 0;
            int top1f = -1, top2f = -1, top3f = -1;
            for (int i = 0; i < frameCount; i++)
            {
                long val = m_PerFrameBuffer[i];
                if (val > top1)
                {
                    top3 = top2; top3f = top2f;
                    top2 = top1; top2f = top1f;
                    top1 = val;  top1f = frameStart + i;
                }
                else if (val > top2)
                {
                    top3 = top2; top3f = top2f;
                    top2 = val;  top2f = frameStart + i;
                }
                else if (val > top3)
                {
                    top3 = val;  top3f = frameStart + i;
                }
            }

            int topCount = top1f >= 0 ? (top2f >= 0 ? (top3f >= 0 ? 3 : 2) : 1) : 0;
            if (topCount > 0)
            {
                group.TopWorstFrameIndices = new int[topCount];
                group.TopWorstFrameBytes = new long[topCount];
                if (topCount >= 1) { group.TopWorstFrameIndices[0] = top1f; group.TopWorstFrameBytes[0] = top1; }
                if (topCount >= 2) { group.TopWorstFrameIndices[1] = top2f; group.TopWorstFrameBytes[1] = top2; }
                if (topCount >= 3) { group.TopWorstFrameIndices[2] = top3f; group.TopWorstFrameBytes[2] = top3; }
            }
```

**Important:** This top-3 extraction must happen BEFORE the median packing section (line 1848) which overwrites `m_PerFrameBuffer` in-place.

**Step 3: Build formatted strings for top worst frames**

In the formatted string section of `BuildGrouping()` (after line 1752, inside the `if (g.MaxBytesPerFrame > 0)` block), add:

```csharp
                    if (g.TopWorstFrameIndices != null)
                    {
                        g.FormattedTopWorst = new string[g.TopWorstFrameIndices.Length];
                        for (int tw = 0; tw < g.TopWorstFrameIndices.Length; tw++)
                        {
                            m_SharedSB.Clear();
                            m_SharedSB.Append("frame ");
                            m_SharedSB.Append(g.TopWorstFrameIndices[tw].ToString());
                            m_SharedSB.Append(" — ");
                            m_SharedSB.Append(FormatBytes(g.TopWorstFrameBytes[tw]));
                            g.FormattedTopWorst[tw] = m_SharedSB.ToString();
                        }
                    }
```

In the `else` branch (line 1756), add:

```csharp
                    g.FormattedTopWorst = null;
```

**Step 4: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: compute top 3 worst frames per callsite group during grouping"
```

---

### Task 2: Add NavigateToFrame helper method

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs:2552-2571` (near SelectInCpuModule)

**Step 1: Add NavigateToFrame method**

Insert after `SelectInCpuModule()` (after line 2571), before the script-opening section comment:

```csharp
        void NavigateToFrame(int frameIndex)
        {
            EnsureProfilerRef();
            if (m_ProfilerWindow == null) return;
            try { m_ProfilerWindow.selectedFrameIndex = frameIndex; }
            catch (Exception e)
            {
                Debug.LogWarning(string.Concat(
                    "[GC Alloc Analyzer] Frame navigation failed frame=",
                    frameIndex.ToString(), ": ", e.Message));
            }
        }
```

This mirrors the pattern already used in the graph bar click handler (lines 814–823).

**Step 2: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: add NavigateToFrame helper for lightweight profiler frame jumps"
```

---

### Task 3: Build per-frame stats and top worst frames UI containers in BuildRightPanel

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs:80` (field declarations)
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs:1317-1319` (BuildRightPanel, between m_MarkerStatsLabel and Call Stack)

**Step 1: Add field declarations**

At line 80, where the existing marker labels are declared, add a new line after:

```csharp
        Label m_MarkerNameLabel, m_MarkerSourceLabel, m_MarkerStatsLabel;
```

Add:

```csharp
        VisualElement m_PerFrameStatsContainer, m_TopWorstContainer;
        Label m_FirstFrameLabel, m_MedianMeanLabel, m_MinFrameLabel, m_MaxFrameLabel;
        Label[] m_TopWorstLabels;
```

**Step 2: Build Per-Frame Statistics container**

In `BuildRightPanel()`, after `siteFoldout.Add(m_MarkerStatsLabel);` (line 1317) and before the `// Call Stack` section (line 1319), insert:

```csharp
            // ── Per-Frame Statistics ──
            m_PerFrameStatsContainer = new VisualElement
            {
                style = { marginBottom = 4, borderTopWidth = 1,
                    borderTopColor = new Color(0.2f, 0.2f, 0.2f), paddingTop = 4 }
            };
            m_PerFrameStatsContainer.Add(new Label("Per-Frame Statistics")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11, marginBottom = 2 }
            });

            m_FirstFrameLabel = new Label("")
            {
                style = { fontSize = 11, color = k_SubtleText, marginBottom = 1 }
            };
            m_PerFrameStatsContainer.Add(m_FirstFrameLabel);

            m_MedianMeanLabel = new Label("")
            {
                style = { fontSize = 11, marginBottom = 1 }
            };
            m_PerFrameStatsContainer.Add(m_MedianMeanLabel);

            // Min frame — clickable
            m_MinFrameLabel = new Label("")
            {
                style = { fontSize = 11, marginBottom = 1 }
            };
            m_MinFrameLabel.RegisterCallback<ClickEvent>(OnMinFrameClicked);
            m_MinFrameLabel.RegisterCallback<MouseEnterEvent>(OnFrameLinkEnter);
            m_MinFrameLabel.RegisterCallback<MouseLeaveEvent>(OnFrameLinkLeave);
            m_PerFrameStatsContainer.Add(m_MinFrameLabel);

            // Max frame — clickable
            m_MaxFrameLabel = new Label("")
            {
                style = { fontSize = 11, marginBottom = 1 }
            };
            m_MaxFrameLabel.RegisterCallback<ClickEvent>(OnMaxFrameClicked);
            m_MaxFrameLabel.RegisterCallback<MouseEnterEvent>(OnFrameLinkEnter);
            m_MaxFrameLabel.RegisterCallback<MouseLeaveEvent>(OnFrameLinkLeave);
            m_PerFrameStatsContainer.Add(m_MaxFrameLabel);

            m_PerFrameStatsContainer.style.display = DisplayStyle.None;
            siteFoldout.Add(m_PerFrameStatsContainer);

            // ── Top Worst Frames ──
            m_TopWorstContainer = new VisualElement
            {
                style = { marginBottom = 4, borderTopWidth = 1,
                    borderTopColor = new Color(0.2f, 0.2f, 0.2f), paddingTop = 4 }
            };
            m_TopWorstContainer.Add(new Label("Top Worst Frames")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11, marginBottom = 2 }
            });

            m_TopWorstLabels = new Label[3];
            for (int i = 0; i < 3; i++)
            {
                m_TopWorstLabels[i] = new Label("")
                {
                    style = { fontSize = 11, marginBottom = 1 }
                };
                m_TopWorstLabels[i].RegisterCallback<ClickEvent>(OnTopWorstClicked);
                m_TopWorstLabels[i].RegisterCallback<MouseEnterEvent>(OnFrameLinkEnter);
                m_TopWorstLabels[i].RegisterCallback<MouseLeaveEvent>(OnFrameLinkLeave);
                m_TopWorstContainer.Add(m_TopWorstLabels[i]);
            }

            m_TopWorstContainer.style.display = DisplayStyle.None;
            siteFoldout.Add(m_TopWorstContainer);
```

**Step 3: Add click and hover callbacks**

Insert these near the other click handlers (after `NavigateToFrame`, before the script-opening section):

```csharp
        static void OnFrameLinkEnter(MouseEnterEvent evt)
        {
            ((VisualElement)evt.target).style.color = k_LinkBlue;
        }

        static void OnFrameLinkLeave(MouseLeaveEvent evt)
        {
            ((VisualElement)evt.target).style.color = StyleKeyword.Null;
        }

        void OnMinFrameClicked(ClickEvent evt)
        {
            if (evt.target is VisualElement ve && ve.userData is int frame)
                NavigateToFrame(frame);
        }

        void OnMaxFrameClicked(ClickEvent evt)
        {
            if (evt.target is VisualElement ve && ve.userData is int frame)
                NavigateToFrame(frame);
        }

        void OnTopWorstClicked(ClickEvent evt)
        {
            if (evt.target is VisualElement ve && ve.userData is int frame)
                NavigateToFrame(frame);
        }
```

**Note:** `OnFrameLinkEnter`/`OnFrameLinkLeave` mirror the existing `OnSortHeaderEnter`/`OnSortHeaderLeave` pattern at lines 1207–1215.

**Step 4: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: build per-frame stats and top worst frames UI containers in right panel"
```

---

### Task 4: Populate the new UI sections in UpdateMarkerSummary and ClearMarkerSummary

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs:2278-2323` (UpdateMarkerSummary)
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs:2445-2454` (ClearMarkerSummary)

**Step 1: Populate per-frame stats in UpdateMarkerSummary**

In `UpdateMarkerSummary()`, after `m_MarkerStatsLabel.text = m_SharedSB.ToString();` (line 2313) and before `// Call stack` (line 2315), insert:

```csharp
            // Per-frame statistics
            bool hasPerFrame = group.MaxBytesPerFrame > 0;
            m_PerFrameStatsContainer.style.display = hasPerFrame ? DisplayStyle.Flex : DisplayStyle.None;
            if (hasPerFrame)
            {
                m_SharedSB.Clear();
                m_SharedSB.Append("First seen: frame ");
                m_SharedSB.Append(group.FirstFrame.ToString());
                m_FirstFrameLabel.text = m_SharedSB.ToString();

                m_SharedSB.Clear();
                m_SharedSB.Append("Median/Frame: ");
                m_SharedSB.Append(group.FormattedMedian);
                m_SharedSB.Append("    Mean/Frame: ");
                m_SharedSB.Append(group.FormattedMean);
                m_MedianMeanLabel.text = m_SharedSB.ToString();

                m_SharedSB.Clear();
                m_SharedSB.Append("Min: ");
                m_SharedSB.Append(group.FormattedMin);
                m_SharedSB.Append(" (frame ");
                m_SharedSB.Append(group.MinFrame.ToString());
                m_SharedSB.Append(")");
                m_MinFrameLabel.text = m_SharedSB.ToString();
                m_MinFrameLabel.userData = group.MinFrame;
                m_MinFrameLabel.tooltip = "Click to jump to this frame in the Profiler";

                m_SharedSB.Clear();
                m_SharedSB.Append("Max: ");
                m_SharedSB.Append(group.FormattedMax);
                m_SharedSB.Append(" (frame ");
                m_SharedSB.Append(group.MaxFrame.ToString());
                m_SharedSB.Append(")");
                m_MaxFrameLabel.text = m_SharedSB.ToString();
                m_MaxFrameLabel.userData = group.MaxFrame;
                m_MaxFrameLabel.tooltip = "Click to jump to this frame in the Profiler";
            }

            // Top worst frames
            bool hasTopWorst = group.FormattedTopWorst != null && group.FormattedTopWorst.Length > 0;
            m_TopWorstContainer.style.display = hasTopWorst ? DisplayStyle.Flex : DisplayStyle.None;
            if (hasTopWorst)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (i < group.FormattedTopWorst.Length)
                    {
                        m_TopWorstLabels[i].text = string.Concat((i + 1).ToString(), ". ", group.FormattedTopWorst[i]);
                        m_TopWorstLabels[i].userData = group.TopWorstFrameIndices[i];
                        m_TopWorstLabels[i].tooltip = "Click to jump to this frame in the Profiler";
                        m_TopWorstLabels[i].style.display = DisplayStyle.Flex;
                    }
                    else
                    {
                        m_TopWorstLabels[i].style.display = DisplayStyle.None;
                    }
                }
            }
```

**Step 2: Clear the new sections in ClearMarkerSummary**

In `ClearMarkerSummary()` (line 2445), after `m_MarkerStatsLabel.text = "";` (line 2449), add:

```csharp
            m_PerFrameStatsContainer.style.display = DisplayStyle.None;
            m_TopWorstContainer.style.display = DisplayStyle.None;
```

**Step 3: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: populate per-frame stats and top worst frames in marker summary"
```

---

### Task 5: Manual verification

**Step 1: Verify in Unity**

1. Open Unity project
2. Create test rig: `Tools > GC Alloc Test > Create Test Rig`
3. Enter Play Mode, let it run 100+ frames
4. Open Profiler (`Ctrl+7`), enable `Call Stacks → GC.Alloc`
5. Open analyzer: `Window > Analysis > GC Alloc Analyzer`
6. Set frame range and click Analyze
7. Select a marker in the left panel

**Expected:** Right panel shows:
- Existing: Name, source, stats line
- **New:** Per-Frame Statistics section with First Frame, Median/Mean, clickable Min/Max
- **New:** Top Worst Frames section with up to 3 clickable frame labels
- Existing: Call Stack, Individual Allocations

**Step 2: Test clickable links**

- Click the Min frame link → Profiler should jump to that frame
- Click the Max frame link → Profiler should jump to that frame
- Click any Top Worst Frame label → Profiler should jump to that frame
- Hover over clickable labels → text should turn blue

**Step 3: Test edge cases**

- Select a marker with only 1 frame of allocations → Top Worst should show only 1 entry
- Select a marker with 0 per-frame stats (unlikely but possible) → both sections should be hidden
- Do a domain reload (enter/exit Play Mode) → reselecting a marker should still show enriched data

**Step 4: Final commit**

If any fixes were needed during verification, commit them:

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "fix: address marker summary enrichment issues found during verification"
```
