# Unity 2022 UI Toolkit Render-Host Plan

For the concise explanation, expected code scope, and risk map, start with [`Documentation~/architecture/unity-2022-render-host-summary.md`](../architecture/unity-2022-render-host-summary.md).

## Status and scope

Implemented for the demonstrated dense bar-geometry failure. Production changes remain limited to Unity API compatibility and the rendering-host boundary. The temporary vertex budgets, rendering-detail reductions, hover, hit-testing, selection, and axis-label workarounds remain removed.

The implementation adds a Strategy boundary between `BarGraphElement` and Unity's rendering surface. Unity 6 delegates to a backend that preserves the current single-element renderer; Unity 2022 delegates to a separate paged backend. It does not replace chart policy or move data and interaction behavior into the backends.

## Problem and evidence

`FlushQuads()` limits each `MeshGenerationContext.Allocate()` call to 16,383 quads (65,532 vertices). Unity 2022 also rejects excessive aggregate geometry from one `VisualElement`, so dividing one element's quads into additional allocations does not address that failure.

The earlier investigation reported inspecting the installed Unity 2022.3 UI Toolkit assembly: its paint path compares aggregate `totalVertices` with `device.maxVerticesPerPage`, logs `A VisualElement must not allocate more than 65535 vertices`, and discards the element's geometry. Before implementation, preserve a reproducible proof with the exact editor version and assembly/method identity; this plan does not substitute for that evidence or a runtime test.

Unity documents the per-allocation maximum and caches generated content until repaint is needed. It also requires treating the element as read-only during `generateVisualContent`. It does not establish that a scheduled rebuild always runs after layout and before every affected child repaint.

References:

- [Unity 2022.3: MeshGenerationContext.Allocate](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/UIElements.MeshGenerationContext.Allocate.html)
- [Unity 2022.3: VisualElement.generateVisualContent](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/UIElements.VisualElement-generateVisualContent.html)
- [Unity 2022.3: Generate 2D visual content](https://docs.unity3d.com/2022.3/Documentation/Manual/UIE-generate-2d-visual-content.html)

## Frozen architectural contracts

1. `ChartDataModel` continues to own bars, segments, and overlays. Pages store geometry ranges, never a second data model or a page-specific bar/segment map.
2. `ChartViewState.DisplayToData` and `DataToDisplay`, rebuilt by the existing `EnsureSortMap()`, remain the only chart order mapping. Preserve comparator, tie behavior, and lazy rebuild semantics. Do not add a view revision to `ChartViewState` for rendering.
3. Keep one shared implementation of `DrawDirectBars()` and `DrawLodBars()`, preserving calculations, iteration order, existing sub-pixel merging, representative colors, overlay suppression, and delegate invocation behavior. Backends may call shared internal rendering stages; they must not implement parallel chart algorithms.
4. Preserve `HitTestBar()`, `HitTestSegment()`, input handlers, `BarGraphEventBus`, `PerFrameGraphController`, and `ProfilerSelectionHandler` unchanged. Pages never participate in picking or hit-testing. Existing LOD segment misses and sub-pixel hit exclusions remain in force.
5. Preserve selection, focus, hover, tag-filter, callback order/context values, label, and profiler behavior. Page boundaries cannot drop, merge, recolor, resize, or reorder additional geometry. Cross-hook Painter2D state is not portable: each callback configures the state it uses.
6. Pool pages and reusable storage. Measure managed allocations after warm-up for repaint and interaction separately; do not claim the entire existing chart is allocation-free (the current sort comparer, for example, can allocate).

## Rendering-backend boundary

`BarGraphElement` depends on an internal `IBarGraphRenderBackend` selected once by `BarGraphRenderBackendFactory`. This is a Strategy boundary, not a general-purpose rendering framework.

```csharp
internal interface IBarGraphRenderBackend
{
    void Attach();
    void Invalidate();
    void Detach();
}
```

The factory contains the only version conditional:

```csharp
#if UNITY_6000_0_OR_NEWER
return new Unity6RenderBackend(owner);
#else
return new Unity2022RenderBackend(owner);
#endif
```

`Unity6RenderBackend` owns the current root `generateVisualContent` subscription and invokes the current single-element render path. Its purpose is to freeze and protect known behavior, not to introduce a new Unity 6 renderer.

`Unity2022RenderBackend` owns the internal child hierarchy, active page ranges, repaint delivery, and attachment lifecycle needed for the older UI Toolkit limit. It calls the same shared plot calculations, direct/LOD generation, callbacks, and highlight stages. It does not own or reinterpret chart data.

The interface may be extended only with evidence from the proof fixtures. Do not add chart-specific methods, an invalidation taxonomy, or a second data model preemptively. If a private frame transport is required, it contains only already ordered geometry ranges and shared layout values; it is not a source of truth for interaction.

This makes the code open to another Unity rendering strategy while keeping the chart behavior closed to version-specific modification.

### Quad delivery

Reuse the existing `QuadData` sequence and mesh-writing logic, including vertex order, indices, texture, UVs, tint, and `Vertex.nearZ`. Each mesh page consumes a contiguous range. Concatenating pages in paint order must reproduce the exact original sequence. Resetting one page's write count must not invalidate later pages; keep the published populated count separate from the next build's cursor.

The candidate quad-only page capacity is `floor(65,535 / 4) = 16,383`. Prove this with actual styles, clipping, and hierarchy in Unity 2022.3. Pages have no background, border, text, or additional Painter2D work. Measure any engine overhead and include it in the capacity calculation; do not introduce an arbitrary quality budget. Test full pages, partial final pages, empty output, growth, and shrinkage.

### Ordered stages, not geometry categories

The current source is the ordering authority:

| Order | Existing operation | Hosting requirement |
|---|---|---|
| 1 | Background, grid, `OnDrawBeforeBars` | Before bars; configurable grid count is not intrinsically bounded |
| 2 | Primary bars including current segment tag tint, then enabled overlay bars | Contiguous quad pages in existing stream order |
| 3 | `OnDrawAfterBars` | After final bar page |
| 4 | Selected-bar fills, then selected-run rims | May need pages |
| 5 | Focus ring, hovered-bar tint, selected-segment highlight | Preserve order and conditions |
| 6 | Tag outlines | May need pages; after selected segment, before hovered segment |
| 7 | Hovered-segment highlight | After tag outlines |
| 8 | Drag rectangle, four padding overdraw strips, axes, `OnDrawAfterChrome` | Preserve masking and final hook position |
| 9 | Existing label overlay and added children | Preserve existing relative sibling order and lifecycle |

Do not place all unbounded highlights beneath a foreground focus/hover layer: that changes compositing. Every page of a stage must precede the next stage. Preserve padding overdraw; replacing it with a plot clip changes current callback/highlight masking.

Internal elements must be non-focusable with `PickingMode.Ignore` on every relevant descendant. Absolute positioning alone does not establish identical root-local coordinates: test actual content/padding/border origins, resize, DPI, opacity, transforms, and clipping. Keep root input targeting, pointer capture, and event coordinates. Preserve externally added children and labels rather than implicitly moving them through a new content container.

### Painter2D output is a separate proof obligation

`DrawHighlights()` is not a quad stream. It uses OddEven fills, strokes, merged selection runs, and overlapping outlines. Replacing these with quads is not a mechanical host extraction: antialiasing, joins, alpha accumulation, and overlap can differ.

The proof must establish a delivery technique preserving those primitives. Splitting Painter2D paths needs evidence for worst-case tessellated vertex count and cross-page fill/blending equivalence. Counting rectangles or paths alone does not establish vertex safety. Do not split a selected run's stroke at a page boundary. The same analysis applies to enabled tag outlines and `GridLineCount`, which the settings class does not cap.

Until this passes, full-fidelity design is incomplete. Do not disable outlines, reduce grid counts, add LOD, or claim bar-fill paging solves every built-in path. If preservation requires a broader renderer rewrite, return to design review instead of silently expanding compatibility work.

### Draw callbacks

Keep delegates and `BarGraphDrawContext` unchanged. Construct and consume contexts only inside the relevant generate callback; never retain `MeshGenerationContext` or `Painter2D` in a frame record.

Dedicated callback elements preserve visual order, coordinates, repaint behavior, and context values. Unity 6 still invokes the hooks on one painter, while Unity 2022 uses separate stage elements. `BarGraphDrawCallback` therefore requires each hook to configure the Painter2D state it uses; carrying a path or painter state between hooks is not part of the portable contract. `OnDrawBeforeBars` still executes before `BarVisualProvider` and `TagHighlightFilter` evaluation.

Callbacks expose Unity's raw API, so arbitrary third-party output cannot be guaranteed below an element's limit. Document this inherited limitation separately from built-in renderer completeness. A dedicated callback element isolates its budget only if callback compatibility first passes.

## Repaint, publication, and lifecycle

Hierarchy/style changes occur outside `generateVisualContent`. Scheduling is a candidate preparation mechanism, not a proven implementation. Resolve publication and invalidation before adding production pages, not afterward.

Inventory all existing invalidation sources:

- Data/overlay notifications, sort, zoom/pan, selection, focus, hover, dragging, visual overrides, custom style resolution, geometry changes, attach/detach.
- Formatter, draw-hook, visual-provider, and tag-filter setters across both partial files.
- Public mutable `Settings`/`ViewState` and delegate-captured state followed by `MarkDirtyRepaint()`. `PerFrameGraphController` already calls `m_BarGraph.MarkDirtyRepaint()` directly. Callers typed as `VisualElement` must keep working. Hiding a nonvirtual method or changing only internal repaint calls is insufficient.
- Unity-initiated layout/style/panel repaints.

For each input, establish when layout is final, what is prepared, which elements are dirtied, and which generation they consume. Required invariants:

1. Bar pages, highlights, callback layout values, and labels presented together use coherent chart inputs/layout. Never combine new buffers with old ranges or retain old geometry after shrinking to zero.
2. Published buffers remain immutable while pages can consume them. Prove main-thread consumption/publication order or use pooled alternate storage; a generation number alone does not prevent overwrite.
3. Invalidation during a queued/running build survives marking that build complete. Invalid size/no data explicitly clears output and relevant labels.
4. Root repaint regenerates affected child content, including captured-delegate changes. Do not assume root dirtying recursively invalidates cached child geometry.
5. Sort/resize/zoom/data changes must not leave geometry an extra scheduled frame behind the mapping read by unchanged hit-testing. Probe events between scheduling, layout, preparation, and repaint. Do not compensate by delaying input, changing hover policy, or adding page hit-tests.
6. Own one preparation/label scheduling lifecycle, pause on detach, and resume without duplication. Retired ranges/callbacks must not retain panels or old data. No page creation or style updates during paint.

If supported APIs and unchanged callers cannot satisfy these conditions, the scheduled design has failed its proof. Revise the host before implementation; do not implicitly accept stale frames or require a new public refresh API.

## Proof and implementation sequence

### Phase 0 — Isolated proof, no production renderer changes

In disposable Editor fixtures, run these on exact recorded Unity 2022.3 and Unity 6 versions:

1. Reproduce the aggregate failure with individually legal allocations, then emit identical geometry through children at proposed capacity.
2. Verify compositing, non-picking descendants, capture, coordinates, clipping, padding masking, resize, DPI, the embedded overview chart, and added children.
3. Prove highlight/grid tessellation capacity and page-boundary fill/stroke parity, including translucent overlaps and visible tag outlines.
4. Trace scheduling/layout/paint order and every repaint source above. Exercise callback state/order, root-only repaint through `VisualElement`, dirty-during-build, rapid sort toggles, and detach/reattach. Retain traces and screenshots.

The automated fixture now covers item 1 and the root-only repaint path with real non-batch rendering. The remaining visual and profiler-integration items stay in the manual verification matrix; they are not inferred from compilation.

### Phase 1 — Introduce the backend seam without changing Unity 6

When implementation is separately undertaken, capture baseline quad/mesh output from the unchanged renderer. Add the backend interface and factory, then move only ownership of the existing root paint subscription into `Unity6RenderBackend`. Unity 6 output and event traces must remain identical before any Unity 2022 backend is connected. Keep model/view, draw algorithms, hit tests, events, and application handlers in place.

### Phase 2 — Implement the Unity 2022 backend

Add `Unity2022RenderBackend`, pooling, ranges, ordered stage delivery, and the proven preparation/publication lifecycle as one coherent change. Preserve visibility/style conditions; verify grow/shrink/empty transitions. Do not modify `Unity6RenderBackend` to accommodate 2022 behavior.

### Phase 3 — Validate parity and host policy

Run the same parity suite against both backends. Keep the explicit version strategies even if the implementations later share additional delivery helpers; this prevents Unity 2022 constraints from silently changing the Unity 6 path.

Compile-time branches remain about API shape: legacy UXML registration versus `[UxmlElement]`, and `sortingEnabled` versus `sortingMode`.

## Parity oracle and acceptance

The oracle is the same input trace on the existing Unity 6 renderer plus known fixture identities. Paging alone does not guarantee Order by Size or marker-selection parity.

Use unequal frame totals, tied totals, single/multi-segment bars, repeated marker names with distinct callsites/tags where available, zeros, sub-pixel segments, and a visible 262,144-byte segment whose bar changes position when sorted. Cross page boundaries within a bar, between bars, and between primary/overlay output. Compare normal order, ByValue descending/ascending, and toggles back; retain the existing comparator's tie behavior.

| Area | Required evidence on both versions |
|---|---|
| Geometry | Concatenated pages equal baseline positions, sizes, colors, order, and mesh values; screenshots verify chrome |
| Mapping | `DataToDisplay[DisplayToData[d]] == d` for active bars; labels, providers, overlays, and filters use baseline indices |
| Segment interaction | Interior points of visible segments produce baseline `(BarDataIndex, BarDisplayIndex, SegmentIndex, Tag, Value, Color)` events; assert the 262,144-byte fixture's value/tag, including immediately after sort/resize/zoom |
| Existing LOD | Both sides of stride 1, sub-pixel thresholds, gaps, clipping, and boundaries match baseline; do not require segment selection where baseline returns a miss |
| Profiler integration | Drive actual pointer move/press/release, keyboard, and drag through unchanged handlers; compare frame `FullFrameStart + BarDataIndex`, tag-to-method lookup, selection callback, and actual profiler result |
| Dense chrome | Alternating selection, long runs, all matching tag outlines, configured high grid counts, overlapping focus/hover/selection remain visible without vertex errors |
| Transitions | Data/overlay replacement, rapid sorting, pan/zoom/reset/overview drag, root-only repaint, resize/DPI, empty state, detach/reattach, reload/reopen have no stale output |
| Performance | Record repaint/interaction CPU, page count, retained memory, and managed allocations after warm-up; distinguish existing sort/provider costs from new host costs |

`ProfilerSelectionHandler.OnReleased()` uses existing hovered-segment state, so directly asserting `HitTestSegment()` is insufficient. `PerFrameGraphController.OnSegmentClicked()` resolves frame and method name, not an individual allocation record by byte value. Do not promise exact allocation-record selection solely because an event contains 262,144 bytes; verify the downstream result supported by the existing path. Report pre-existing selection/mapping defects separately without repairing them in this compatibility change.

The implemented scope is accepted when the original dense-bar reproduction renders without the vertex error and the manual mapping/selection matrix passes. Arbitrary callback output and deliberately extreme Painter2D chrome remain subject to Unity 2022's per-element limit; supporting those would be a separate renderer expansion rather than an unreported quality reduction.
