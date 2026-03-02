# Strip Leading :: and No-Callstack Mode — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix leading `::` in method names when no namespace exists, and make the analyzer useful even when call stacks are not enabled.

**Architecture:** Two independent changes: (1) a simple string-trimming fix in `StripAssembly` in both files, and (2) enriching the no-callstack code path in both the main window and the profiler module to display parent method names and hierarchy paths instead of generic "[no callstack]" text.

**Tech Stack:** Unity 6000.3.6f1, C# 9.0, Unity UIElements, Unity Profiler APIs

**Design doc:** `docs/plans/2026-02-22-strip-leading-colons-and-no-callstack-mode-design.md`

---

### Task 1: Fix `StripAssembly` in GCAllocAnalyzerWindow

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs:1869-1873`

**Step 1: Update `StripAssembly` to strip leading `::`**

Replace the existing method at line 1869:

```csharp
static string StripAssembly(string raw)
{
    int bang = raw != null ? raw.IndexOf('!') : -1;
    return bang >= 0 ? raw.Substring(bang + 1) : raw ?? "";
}
```

With:

```csharp
static string StripAssembly(string raw)
{
    int bang = raw != null ? raw.IndexOf('!') : -1;
    string result = bang >= 0 ? raw.Substring(bang + 1) : raw ?? "";
    if (result.Length > 2 && result[0] == ':' && result[1] == ':')
        result = result.Substring(2);
    return result;
}
```

**Step 2: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "fix: strip leading :: from method names when namespace is empty"
```

---

### Task 2: Fix `StripAssembly` in GCAllocBreakdownModule

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocBreakdownModule.cs:647-651`

**Step 1: Apply the same fix**

Replace the existing method at line 647:

```csharp
static string StripAssembly(string raw)
{
    int bang = raw != null ? raw.IndexOf('!') : -1;
    return bang >= 0 ? raw.Substring(bang + 1) : raw ?? "";
}
```

With:

```csharp
static string StripAssembly(string raw)
{
    int bang = raw != null ? raw.IndexOf('!') : -1;
    string result = bang >= 0 ? raw.Substring(bang + 1) : raw ?? "";
    if (result.Length > 2 && result[0] == ':' && result[1] == ':')
        result = result.Substring(2);
    return result;
}
```

**Step 2: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocBreakdownModule.cs
git commit -m "fix: strip leading :: in profiler module StripAssembly"
```

---

### Task 3: Improve no-callstack display name in main window

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs:908-910`

**Step 1: Change no-callstack display name**

At line 908-910, replace:

```csharp
alloc.DisplayName = resolvedCopy.Count > 0
    ? FormatTopFrame(resolvedCopy[0])
    : string.Concat(parentMethod, "  [no callstack]");
```

With:

```csharp
alloc.DisplayName = resolvedCopy.Count > 0
    ? FormatTopFrame(resolvedCopy[0])
    : parentMethod;
```

The status bar already displays `"⚠ Call Stacks: NOT detected"` when no call stacks are found (line 972), so the suffix is redundant.

**Step 2: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "fix: remove [no callstack] suffix from display names"
```

---

### Task 4: Show hierarchy path in call stack panel when no call stack

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs:1461-1473`

**Step 1: Replace the no-callstack branch in `BuildCallStackDisplay`**

The current no-callstack branch (lines 1465-1473) shows a static "enable call stacks" message. Replace it to display the hierarchy path from the group's first allocation.

Replace:

```csharp
if (group.ResolvedCallStack == null || group.ResolvedCallStack.Count == 0)
{
    m_CallStackContainer.Add(new Label(
        "No call stack available.\nEnable: Profiler toolbar → Call Stacks → GC.Alloc")
    {
        style = { fontSize = 11, color = k_Yellow, whiteSpace = WhiteSpace.Normal }
    });
    return;
}
```

With:

```csharp
if (group.ResolvedCallStack == null || group.ResolvedCallStack.Count == 0)
{
    // Show hierarchy path as a visual breadcrumb trail
    string hierarchy = group.Allocations.Count > 0
        ? group.Allocations[0].HierarchyPath : null;

    if (!string.IsNullOrEmpty(hierarchy))
    {
        m_CallStackContainer.Add(new Label("Profiler Hierarchy")
        {
            style = { fontSize = 10, color = k_SubtleText, marginBottom = 2,
                unityFontStyleAndWeight = FontStyle.Bold }
        });

        string[] segments = hierarchy.Split(new[] { " > " }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            bool isLast = i == segments.Length - 1;
            m_SharedSB.Clear();
            m_SharedSB.Append(isLast ? "→ " : "  ");
            m_SharedSB.Append(segments[i]);

            m_CallStackContainer.Add(new Label(m_SharedSB.ToString())
            {
                style = { fontSize = 11, color = isLast ? k_TopFrame : k_CallerFrame,
                    paddingTop = 1, paddingBottom = 1 }
            });
        }
    }

    m_CallStackContainer.Add(new Label("Enable Call Stacks in Profiler toolbar for full detail.")
    {
        style = { fontSize = 10, color = k_SubtleText, marginTop = 4,
            whiteSpace = WhiteSpace.Normal, fontStyleAndWeight = FontStyle.Italic }
    });
    return;
}
```

**Step 2: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocAnalyzerWindow.cs
git commit -m "feat: show hierarchy path in details panel when call stacks unavailable"
```

---

### Task 5: Add hierarchy tracking to module extraction

**Files:**
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocBreakdownModule.cs:81-85` (add depth stack buffer)
- Modify: `Assets/GCAllocAnalyzer/Editor/GCAllocBreakdownModule.cs:225-285` (restructure sample loop)

**Step 1: Add depth stack buffer and DepthEntry struct**

At line 85, after the `m_GroupDict` declaration, add:

```csharp
readonly List<DepthEntry> m_DepthStack = new(32);
```

At line 713, after the `ResolvedFrame` struct, add:

```csharp
class DepthEntry
{
    public string Name;
    public int Remaining;
}
```

**Step 2: Restructure the sample loop in `ExtractFrame`**

The current loop (lines 225-285) skips non-GC.Alloc samples with `continue`. It needs to process all samples to track hierarchy depth, mirroring the main window's approach.

Replace the inner loop (lines 225-285):

```csharp
for (int i = 0; i < raw.sampleCount; i++)
{
    if (raw.GetSampleMarkerId(i) != gcAllocId) continue;
    long bytes = raw.GetSampleMetadataAsLong(i, 0);
    if (bytes <= 0) continue;

    totalBytes += bytes;
    totalCount++;

    m_FrameBuf.Clear();
    m_AddrBuf.Clear();
    raw.GetSampleCallstack(i, m_AddrBuf);

    if (m_AddrBuf.Count > 0)
    {
        anyCS = true;
        for (int a = 0; a < m_AddrBuf.Count; a++)
        {
            var info = raw.ResolveMethodInfo(m_AddrBuf[a]);
            if (string.IsNullOrEmpty(info.methodName)) continue;
            m_FrameBuf.Add(new ResolvedFrame
            {
                RawMethodName = info.methodName.Trim(),
                SourceFile = (info.sourceFileName ?? "").Trim(),
                SourceLine = (int)info.sourceFileLine
            });
        }
    }

    string key;
    string display;
    List<ResolvedFrame> stackCopy = null;

    if (m_FrameBuf.Count > 0)
    {
        key = BuildKey(m_FrameBuf);
        display = FormatTopFrame(m_FrameBuf[0]);
        stackCopy = new List<ResolvedFrame>(m_FrameBuf.Count);
        for (int fc = 0; fc < m_FrameBuf.Count; fc++)
            stackCopy.Add(m_FrameBuf[fc]);
    }
    else
    {
        key = string.Concat("nostack|", threadName);
        display = string.Concat("[no callstack — ", threadName, "]");
    }

    if (!m_GroupDict.TryGetValue(key, out var g))
    {
        g = new FrameGroup
        {
            Key = key,
            DisplayName = display,
            CallStack = stackCopy
        };
        m_GroupDict[key] = g;
        groups.Add(g);
    }
    g.TotalBytes += bytes;
    g.Count++;
}
```

With:

```csharp
m_DepthStack.Clear();

for (int i = 0; i < raw.sampleCount; i++)
{
    int markerId = raw.GetSampleMarkerId(i);
    int childCount = raw.GetSampleChildrenCount(i);
    string sampleName = raw.GetSampleName(i);

    // Maintain depth stack
    while (m_DepthStack.Count > 0 &&
           m_DepthStack[m_DepthStack.Count - 1].Remaining <= 0)
        m_DepthStack.RemoveAt(m_DepthStack.Count - 1);

    if (m_DepthStack.Count > 0)
        m_DepthStack[m_DepthStack.Count - 1].Remaining--;

    if (markerId == gcAllocId)
    {
        long bytes = raw.GetSampleMetadataAsLong(i, 0);
        if (bytes > 0)
        {
            totalBytes += bytes;
            totalCount++;

            m_FrameBuf.Clear();
            m_AddrBuf.Clear();
            raw.GetSampleCallstack(i, m_AddrBuf);

            if (m_AddrBuf.Count > 0)
            {
                anyCS = true;
                for (int a = 0; a < m_AddrBuf.Count; a++)
                {
                    var info = raw.ResolveMethodInfo(m_AddrBuf[a]);
                    if (string.IsNullOrEmpty(info.methodName)) continue;
                    m_FrameBuf.Add(new ResolvedFrame
                    {
                        RawMethodName = info.methodName.Trim(),
                        SourceFile = (info.sourceFileName ?? "").Trim(),
                        SourceLine = (int)info.sourceFileLine
                    });
                }
            }

            string key;
            string display;
            List<ResolvedFrame> stackCopy = null;

            if (m_FrameBuf.Count > 0)
            {
                key = BuildKey(m_FrameBuf);
                display = FormatTopFrame(m_FrameBuf[0]);
                stackCopy = new List<ResolvedFrame>(m_FrameBuf.Count);
                for (int fc = 0; fc < m_FrameBuf.Count; fc++)
                    stackCopy.Add(m_FrameBuf[fc]);
            }
            else
            {
                string parentMethod = m_DepthStack.Count > 0
                    ? m_DepthStack[m_DepthStack.Count - 1].Name : "<root>";
                key = string.Concat("nostack|", parentMethod);
                display = parentMethod;
            }

            if (!m_GroupDict.TryGetValue(key, out var g))
            {
                g = new FrameGroup
                {
                    Key = key,
                    DisplayName = display,
                    CallStack = stackCopy
                };
                m_GroupDict[key] = g;
                groups.Add(g);
            }
            g.TotalBytes += bytes;
            g.Count++;
        }
    }

    if (childCount > 0)
        m_DepthStack.Add(new DepthEntry { Name = sampleName, Remaining = childCount });
}
```

**Step 3: Commit**

```bash
git add Assets/GCAllocAnalyzer/Editor/GCAllocBreakdownModule.cs
git commit -m "feat: show parent method for no-callstack allocations in profiler module"
```

---

### Task 6: Manual verification

**Step 1: Verify :: stripping**

1. Open the project in Unity 6000.3.6f1
2. Enter Play Mode with the test rig (`Tools > GC Alloc Test > Create Test Rig`)
3. Open Profiler (`Ctrl+7`), do NOT enable Call Stacks
4. Build for IL2CPP (or use an existing IL2CPP `.raw` capture)
5. Open `Window > Analysis > GC Alloc Analyzer`, Pull Data, Analyze
6. Confirm: no entries show a leading `::` in the Allocation Site column
7. Confirm: entries WITH namespaces still show `Namespace::Class.Method` format

**Step 2: Verify no-callstack mode (main window)**

1. With Call Stacks disabled in Profiler toolbar, capture some frames
2. Pull Data and Analyze in GC Alloc Analyzer
3. Confirm: allocation sites show parent method names (not `"parentMethod  [no callstack]"`)
4. Confirm: status bar shows `"⚠ Call Stacks: NOT detected"`
5. Select an allocation site — confirm the right panel shows:
   - "Profiler Hierarchy" header
   - Hierarchy path segments styled as a breadcrumb trail
   - Subtle note about enabling call stacks

**Step 3: Verify no-callstack mode (profiler module)**

1. In the Profiler window, select the "GC Alloc Breakdown" module
2. Scrub through frames
3. Confirm: groups show parent method names instead of `"[no callstack — MainThread]"`
4. Confirm: warning banner shows about enabling call stacks

**Step 4: Verify call-stack mode still works**

1. Enable `Call Stacks → GC.Alloc` in Profiler toolbar
2. Re-capture and analyze
3. Confirm: both windows work exactly as before — full call stacks, script opening, etc.

**Step 5: Final commit**

If any fixes were needed during verification, commit them.
