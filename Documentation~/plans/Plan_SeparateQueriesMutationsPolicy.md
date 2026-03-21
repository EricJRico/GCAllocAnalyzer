# Plan: Separate Queries, Mutations, and Policy

## Problem

`BarGraphElement` makes policy decisions that belong to consumers. `InternalSelectBar` couples three concerns: hit-testing, selection mutation, and event notification. You can't get a click notification without modifying selection. You can't modify selection without firing `BarClicked`. Every consumer that wants different click behavior has to work around this coupling.

Real examples:
- **Profiler per-frame graph:** Click bar → navigate to frame (no selection, no dimming). Click segment → select in marker list. Drag → select range with dimming. Currently impossible without hacks because `InternalSelectBar` always adds to `SelectedBars` and fires `BarClicked` together.
- **GC Analyzer:** Click segment → select allocation site. Click empty → clear selection. Currently routes through `InternalSelectBar` which fires `BarClicked` even when the consumer only wants segment selection.
- **Future consumers:** Toggle selection, double-click drill-down, right-click context menu — all blocked by the element deciding what a click means.

## Architecture

The element provides **queries** and **mutations**. Handlers provide **policy**. The element never interprets input — it just answers questions and changes state when told to.

### Element provides:

**Queries** (stateless, public, no side effects):
```
HitTestBar(Vector2 localPos) → int dataIndex
HitTestSegment(Vector2 localPos, int barDataIndex) → SegmentHitResult
GetSegmentCount(int barDataIndex) → int
```

**Mutations** (state change + `SelectionChanged` event, public):
```
SelectBar(int dataIndex, bool additive)
SelectSegment(int barDataIndex, int segmentIndex)
ClearSelection()
```

**Read-only data accessors** (public, forwarded from `ChartDataModel`):
```
Bars         → BarEntry[]    (read-only by contract, not enforcement)
Segments     → BarSegment[]  (read-only by contract, not enforcement)
BarCount     → int           (already exists)
SegmentCount → int
```

**Note on raw array access:** Returning the backing array means consumers can technically write to it. This matches the existing `ChartDataModel` pattern. The alternative — copying or wrapping in `ReadOnlySpan<T>` — would add per-frame allocation or API complexity for a safety guarantee that internal handlers don't need. The doccomments warn against writing. If external consumers abuse it, revisit.

**State notifications** (events that report state changes, not input):
```
SelectionChanged      — selection set was modified
SegmentHoverChanged   — hovered segment changed (already exists, keep)
HoverChanged          — hovered bar changed (already exists, keep)
ViewChanged           — zoom/pan changed (already exists, keep)
```

### Element does NOT provide:

**Input interpretation events** (move to handlers):
```
BarClicked        — REMOVE from element
SegmentClicked    — REMOVE from element
DragCompleted     — MOVE to BarGraphSelectionHandler
BarClickedUIEvent — REMOVE from element
```

These are policy decisions ("the user intended to click this bar", "a drag gesture completed") that belong in the handler, not the element. Different handlers interpret the same mouse-down differently.

**Why `DragCompleted` moves too:** By the same logic as `BarClicked` and `SegmentClicked`, `DragCompleted` is input interpretation — "a drag gesture completed." It fires from `InternalCommitDragSelection` alongside selection mutation. A custom handler that doesn't want drag-select behavior can't suppress it. It carries the selection result, but `BarClicked` also carries data — the distinction is arbitrary. Move it to the handler for consistency.

### Handlers provide:

Each handler subscribes to `BarGraphEventBus` actions and calls the element's public mutations. Handlers that need to notify consumers about input interpretation define their own events or accept callbacks.

**Default handler (ships with package):**
`BarGraphSelectionHandler` — same behavior as today for backward compatibility:
- Click bar → `SelectBar(idx, additive)`
- Click segment in multi-segment bar → `SelectSegment(barIdx, segIdx)`
- Click single-segment bar → `SelectBar(idx, additive)`
- Drag → `ClearSelection()` then select range

The handler fires its own `BarClicked` / `SegmentClicked` / `DragCompleted` actions that consumers subscribe to on the handler, not on the element.

**Custom handler (profiler example):**
```csharp
public class ProfilerSelectionHandler : IBarGraphHandler
{
    public event Action<int> FrameNavigated;
    public event Action<SegmentHitResult> MarkerSelected;

    private void OnReleased(Vector2 pos)
    {
        if (_dragging)
        {
            // Drag-select for sub-range analysis
            _element.CommitDragSelection(rect, false);
        }
        else
        {
            var seg = _element.HitTestSegment(pos);
            if (seg.IsHit)
            {
                _element.SelectSegment(seg.BarDataIndex, seg.SegmentIndex);
                MarkerSelected?.Invoke(seg);
            }
            else
            {
                int bar = _element.HitTestBar(pos);
                if (bar >= 0)
                {
                    // Navigate to frame — NO selection, no dimming
                    FrameNavigated?.Invoke(bar);
                }
            }
        }
    }
}
```

No hacks. No workarounds. The handler decides the policy.

---

## Implementation Steps

### Step 1: Make queries public

**File:** `BarChart/Core/BarGraphElement.cs`

Change access modifiers:

| Method | Before | After |
|--------|--------|-------|
| `HitTestBar(Vector2)` | `internal` | `public` |
| `HitTestSegment(Vector2, int)` | `internal` | `public` |
| `GetSegmentCount(int)` | `internal` | `public` |

No behavioral change. Just access modifiers.

### Step 2: Create public mutation methods

**File:** `BarChart/Core/BarGraphElement.cs`

Add public methods that wrap the existing internal ones. The internal methods stay for now (handlers in the same assembly still call them) but the public methods become the official API:

```csharp
/// <summary>
/// Adds or removes a bar from the selection set.
/// Clears segment selection. Fires <see cref="SelectionChanged"/>.
/// </summary>
public void SelectBar(int dataIndex, bool additive = false)
    => InternalSelectBar(dataIndex, additive);

/// <summary>
/// Selects a single segment. Clears bar selection.
/// Fires <see cref="SelectionChanged"/>.
/// </summary>
public void SelectSegment(int barDataIndex, int segmentIndex)
    => InternalSelectSegment(barDataIndex, segmentIndex);

/// <summary>
/// Clears all bar and segment selection.
/// Fires <see cref="SelectionChanged"/>.
/// </summary>
public void ClearSelection()
{
    _viewState.SelectedBars.Clear();
    _viewState.SelectedSegmentBar   = -1;
    _viewState.SelectedSegmentIndex = -1;
    _viewState.FocusedBarIndex      = -1;
    FireSelectionChanged();
    MarkDirtyRepaint();
}
```

### Step 3: Remove input events from element

**File:** `BarChart/Core/BarGraphElement.cs`

Remove from the element:
- `public event Action<BarClickedEventArgs> BarClicked`
- `public event Action<SegmentEventArgs> SegmentClicked`
- `public event Action<DragCompletedEventArgs> DragCompleted`
- `BarClickedUIEvent` pooled event and `SendEvent` call
- All `BarClicked?.Invoke(...)`, `SegmentClicked?.Invoke(...)`, and `DragCompleted?.Invoke(...)` calls from `InternalSelectBar`, `InternalSelectSegment`, and `InternalCommitDragSelection`

These mutation methods should only mutate state and fire `SelectionChanged`. They should not interpret what the mutation means.

### Step 4: Extract keyboard navigation into a handler

**New file:** `BarChart/Input/Handlers/BarGraphKeyboardHandler.cs`

The keyboard input is currently embedded directly in `BarGraphElement` via `OnKeyDown` (line 917). It directly mutates `SelectedBars`, fires `BarClicked` from `ActivateFocusedBar`, and handles `SelectAll` and Escape. This violates the architecture — it's policy (what a keypress means) embedded in the element.

Extract into `BarGraphKeyboardHandler : IBarGraphHandler`:

```csharp
public sealed class BarGraphKeyboardHandler : IBarGraphHandler
{
    public event Action<BarClickedEventArgs> BarActivated;

    private BarGraphElement _element;

    public void Register(BarGraphEventBus actions, BarGraphElement element)
    {
        _element = element;
        element.RegisterCallback<KeyDownEvent>(OnKeyDown);
    }

    public void Unregister(BarGraphEventBus actions)
    {
        _element?.UnregisterCallback<KeyDownEvent>(OnKeyDown);
        _element = null;
    }

    private void OnKeyDown(KeyDownEvent evt)
    {
        // Arrow keys → MoveFocus → element.SelectBar
        // Space/Enter → ActivateFocusedBar → fire BarActivated
        // Ctrl+A → SelectAll → element.SelectBar in loop or element.SelectAll()
        // Escape → element.ClearSelection()
        // Ctrl+R → element.ResetView()
    }
}
```

Key changes:
- `SetFocus` calls `element.SelectBar` instead of directly mutating `_viewState.SelectedBars`
- `ActivateFocusedBar` fires `BarActivated` on the handler instead of `BarClicked` on the element
- `SelectAll` calls `element.SelectBar` in a loop (or a new `SelectAll()` public mutation)
- Escape calls `element.ClearSelection()`

Remove from `BarGraphElement`:
- `OnKeyDown`, `MoveFocus`, `SetFocus`, `ActivateFocusedBar`, `SelectAll`
- `RegisterCallback<KeyDownEvent>(OnKeyDown)` in the constructor (line 271)

`EnsureBarVisible` must become a public method on the element since the keyboard handler needs to scroll the viewport to show the focused bar, but it modifies `PanX` which is internal view state.

**Note:** Consider whether `SelectAll()` should be a public mutation on the element. It's used by both keyboard (Ctrl+A) and potentially by consumers. If so, add it alongside `SelectBar`/`SelectSegment`/`ClearSelection`.

### Step 5: Move input events to BarGraphSelectionHandler

**File:** `BarChart/Input/Handlers/BarGraphSelectionHandler.cs`

The handler now fires its own events after calling the element's mutations:

```csharp
public sealed class BarGraphSelectionHandler : IBarGraphHandler
{
    // Input interpretation events — consumers subscribe here
    public event Action<BarClickedEventArgs>    BarClicked;
    public event Action<SegmentEventArgs>       SegmentClicked;
    public event Action<DragCompletedEventArgs> DragCompleted;

    private void OnReleased(Vector2 pos)
    {
        if (_dragging)
        {
            _element.InternalCommitDragSelection(
                MakeRect(_dragStart, pos), _additive);

            DragCompleted?.Invoke(new DragCompletedEventArgs(
                MakeRect(_dragStart, pos), _element.ViewState.SelectedBars));
        }
        else
        {
            var vs = _element.ViewState;

            if (vs.HoveredSegmentBar >= 0 && vs.HoveredSegmentIndex >= 0
                && _element.GetSegmentCount(vs.HoveredSegmentBar) > 1)
            {
                int barIdx = vs.HoveredSegmentBar;
                int segIdx = vs.HoveredSegmentIndex;
                _element.SelectSegment(barIdx, segIdx);

                // Fire segment click notification
                ref readonly var bar = ref _element.Bars[barIdx];
                ref readonly var seg = ref _element.Segments[bar.SegmentStart + segIdx];
                int dispIdx = barIdx < vs.DataToDisplay.Length
                    ? vs.DataToDisplay[barIdx] : barIdx;
                SegmentClicked?.Invoke(new SegmentEventArgs
                {
                    BarDataIndex    = barIdx,
                    BarDisplayIndex = dispIdx,
                    SegmentIndex    = segIdx,
                    Tag             = seg.Tag,
                    Value           = seg.Value,
                    Color           = seg.Color,
                    LocalPosition   = pos,
                });
            }
            else
            {
                _element.SelectBar(_clickedBar, _additive);

                if (_clickedBar >= 0)
                {
                    int dispIdx = _clickedBar < vs.DataToDisplay.Length
                        ? vs.DataToDisplay[_clickedBar] : _clickedBar;
                    float val = _element.Bars[_clickedBar].TotalValue;
                    BarClicked?.Invoke(new BarClickedEventArgs(
                        _clickedBar, dispIdx, val, pos));
                }
            }
        }

        _dragging = false;
    }
}
```

**Note:** This requires `Bars` and `Segments` to be publicly accessible. See Step 7.

### Step 6: Update event wiring pattern

Consumers currently subscribe to events on the element:
```csharp
graph.BarClicked += args => { ... };
graph.SegmentClicked += args => { ... };
```

After the change, they subscribe on the handler:
```csharp
var selHandler = new BarGraphSelectionHandler();
graph.AddHandler(selHandler);
selHandler.BarClicked += args => { ... };
selHandler.SegmentClicked += args => { ... };
```

This is a **breaking change** for consumers. The handler reference must be kept to subscribe to events.

Use `GetHandler<T>` for convenience:
```csharp
graph.AddHandler(new BarGraphSelectionHandler());
// Later:
graph.GetHandler<BarGraphSelectionHandler>().BarClicked += args => { ... };
```

### Step 7: Expose read-only data accessors and add GetHandler

**File:** `BarChart/Core/BarGraphElement.cs`

The handler needs to read bar/segment data to populate event args. Add:

```csharp
/// <summary>Read-only access to the bar array. Do not write past [BarCount-1].</summary>
public BarEntry[] Bars => _model.Bars;

/// <summary>Read-only access to the segment array. Do not write past [SegmentCount-1].</summary>
public BarSegment[] Segments => _model.Segments;

/// <summary>Number of segments in the current dataset.</summary>
public int SegmentCount => _model.SegmentCount;
```

Add `GetHandler<T>`:

```csharp
public T GetHandler<T>() where T : class, IBarGraphHandler
{
    for (int i = 0; i < _handlers.Count; i++)
        if (_handlers[i] is T h) return h;
    return null;
}
```

**Note:** `_handlers` list already exists (line 232). `AddHandler` and `RemoveHandler<T>` already use it. Only the `GetHandler<T>` method is new.

### Step 8: Make RestoreViewSnapshot consistent

**File:** `BarChart/Core/BarGraphElement.cs`

`RestoreViewSnapshot` (line 563) directly mutates `_viewState.SelectedBars`, bypassing the public API. This is intentional for performance — restoring many selected bars shouldn't fire N `SelectionChanged` events. Keep the direct mutation but also clear segment selection and add a comment explaining the bypass:

```csharp
// Direct mutation for bulk restore — avoids N SelectionChanged events.
// Single FireSelectionChanged() call at the end covers the state change.
_viewState.SelectedBars.Clear();
_viewState.SelectedSegmentBar   = -1;
_viewState.SelectedSegmentIndex = -1;
if (snap.SelectedBars != null)
{
    for (int i = 0; i < snap.SelectedBars.Length; i++)
    {
        int idx = snap.SelectedBars[i];
        if (idx >= 0 && idx < barCount)
            _viewState.SelectedBars.Add(idx);
    }
}
```

### Step 9: Update demos

**Files:** All demo panels that subscribe to `BarClicked`, `SegmentClicked`, or `DragCompleted`

Update event subscriptions from:
```csharp
graph.BarClicked += args => Log(...);
graph.SegmentClicked += args => SelectSite(args.Tag);
```

To:
```csharp
var sel = graph.GetHandler<BarGraphSelectionHandler>();
sel.BarClicked += args => Log(...);
sel.SegmentClicked += args => SelectSite(args.Tag);
```

For `BarGraphGalleryDemo.WireEvents`:
```csharp
private void WireEvents(DemoPanel panel)
{
    var name = panel.Title;
    var sel = panel.Graph.GetHandler<BarGraphSelectionHandler>();
    if (sel != null)
    {
        sel.BarClicked += a => Log($"[{name}] BarClicked data={a.DataIndex}");
        sel.DragCompleted += a => Log($"[{name}] DragDone selected={a.SelectedDataIndices.Count}");
    }
    panel.Graph.SelectionChanged += a => Log($"[{name}] Selection count={a.SelectedDataIndices.Count}");
    panel.Graph.HoverChanged     += a => { if (a.DataIndex >= 0) Log($"[{name}] Hover bar={a.DataIndex}"); };
}
```

For `SegmentInteractionDemo`:
```csharp
var sel = _graph.GetHandler<BarGraphSelectionHandler>();
sel.SegmentClicked += OnSegmentClicked;
// SegmentHoverChanged stays on the element — it's state notification, not input interpretation
_graph.SegmentHoverChanged += OnSegmentHover;
```

For `DemoPanel` base class — add keyboard handler in constructor:
```csharp
Graph.AddHandler(new BarGraphKeyboardHandler());
```

### Step 10: Clean up internal methods

Once all handlers use the public API, the `Internal*` methods can optionally be made `private` or removed. However, keeping them `internal` doesn't hurt — they're just implementation details behind the public wrappers.

The `BarClickedUIEvent` pooled event that bubbles through the visual tree is removed entirely. If consumers need visual-tree event bubbling, they can send their own event from their handler.

---

## Dependencies

**Segment selection dimming:** The dim alpha code in `DrawDirectBars` (line 1211) only checks `_viewState.SelectedBars.Count > 0`. When a segment is selected via `InternalSelectSegment`, `SelectedBars` is cleared, so `hasSel` is false and no dimming occurs. The render-time dim selection plan's Step 3 must be implemented for segment selection to trigger dimming. This is not blocking for the architecture refactor but is a known gap.

---

## Files Changed

| File | Change |
|------|--------|
| `BarGraphElement.cs` | Public queries, public mutations, `ClearSelection`, `GetHandler<T>`, `EnsureBarVisible` made public, data accessors, remove `BarClicked`/`SegmentClicked`/`DragCompleted` events, remove `BarClickedUIEvent`, remove `OnKeyDown`/`MoveFocus`/`SetFocus`/`ActivateFocusedBar`/`SelectAll`, remove `KeyDownEvent` registration |
| `BarGraphKeyboardHandler.cs` | NEW — extracted keyboard navigation handler |
| `BarGraphSelectionHandler.cs` | Owns `BarClicked`/`SegmentClicked`/`DragCompleted` events, fires them after calling public mutations |
| `BarGraphGalleryDemo.cs` | Subscribe to handler events instead of element events |
| `SegmentInteractionDemo.cs` | Subscribe to handler events instead of element events |
| `DemoPanel.cs` | Add `BarGraphKeyboardHandler` to handler setup |
| `BarGraphEvents.cs` | Remove `BarClickedUIEvent` class (optional — can deprecate instead) |
| `IBarGraphHandler.cs` | No change |
| `BarGraphEventBus.cs` | No change |
| `BarGraphHoverHandler.cs` | No change (hover is state notification, stays on element) |

---

## What Does NOT Change

- `SelectionChanged` — stays on the element (state notification)
- `SegmentHoverChanged` — stays on the element (state notification)
- `HoverChanged` — stays on the element (state notification)
- `ViewChanged` — stays on the element (state notification)
- `BarGraphHoverHandler` — unchanged
- `BarGraphPanHandler` — unchanged
- `BarGraphZoomHandler` — unchanged
- `BarGraphUIToolkitInput` — unchanged
- Rendering, hit testing logic, USS properties — unchanged
- `BarVisualProvider` / draw callbacks — unchanged

---

## Breaking Changes

This is a breaking change for any consumer that subscribes to `graph.BarClicked`, `graph.SegmentClicked`, or `graph.DragCompleted`. The migration is mechanical:

1. Keep a reference to the handler (or use `GetHandler<T>`)
2. Subscribe to the same-named event on the handler instead of the element
3. `SelectionChanged`, `HoverChanged`, `SegmentHoverChanged` stay on the element — no change needed for those
4. Keyboard navigation requires adding `BarGraphKeyboardHandler` — `DemoPanel` base class should add it by default

---

## Why This Ordering

Steps 1-2 are additive (new public API, nothing removed). Steps 3-5 are the breaking changes (events move, keyboard extracts). Steps 6-9 are migration. Step 10 is cleanup.

This means you can land Steps 1-2 first, update consumers to use the new API, then land Steps 3-5 when ready. Or ship it all at once if you control all consumers.

---

## Verification

1. Default handler (`BarGraphSelectionHandler`) — click, drag-select, segment click all work identically to current behavior
2. Keyboard handler (`BarGraphKeyboardHandler`) — arrow keys, Space/Enter, Ctrl+A, Escape all work identically to current behavior
3. Custom handler — can call `HitTestBar` → decide policy → call `SelectBar` or not, without forced selection
4. Profiler use case — click bar fires `FrameNavigated` without adding to `SelectedBars`, no dimming
5. `SelectionChanged` fires on all selection mutations regardless of which handler triggered them
6. `SegmentHoverChanged` still fires from the element on mouse move
7. `GetHandler<T>()` returns the correct handler or null
8. Demos compile and work with updated event subscriptions
9. Keyboard handler not added → no keyboard navigation, no crash
10. `RestoreViewSnapshot` correctly restores selection without firing per-bar events
11. No performance regression — public method wrappers are trivial forwarding calls
