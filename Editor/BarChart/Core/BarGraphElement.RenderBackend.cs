using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace GCAllocBreakdown.BarChart.Core
{
    public sealed partial class BarGraphElement
    {
        private interface IBarGraphRenderBackend
        {
            void Attach();
            void Invalidate();
            void Detach();
        }

        private static class BarGraphRenderBackendFactory
        {
            public static IBarGraphRenderBackend Create(BarGraphElement owner)
            {
#if UNITY_6000_0_OR_NEWER
                return new Unity6RenderBackend(owner);
#else
                return new Unity2022RenderBackend(owner);
#endif
            }
        }

        /// <summary>
        /// Unity 6 keeps the original single-element render callback. This class is
        /// deliberately only a lifecycle adapter so the established render path is
        /// not affected by the Unity 2022 workaround.
        /// </summary>
        private sealed class Unity6RenderBackend : IBarGraphRenderBackend
        {
            private readonly BarGraphElement _owner;

            public Unity6RenderBackend(BarGraphElement owner) => _owner = owner;

            public void Attach() => _owner.generateVisualContent += _owner.OnGenerateVisualContent;

            public void Invalidate() { }

            public void Detach() => _owner.generateVisualContent -= _owner.OnGenerateVisualContent;
        }

#if !UNITY_6000_0_OR_NEWER
        /// <summary>
        /// Unity 2022/2023 render backend. UI Toolkit applies its UInt16 vertex
        /// ceiling to all geometry emitted by one VisualElement, so bar quads are
        /// delivered by pooled child elements. Chart data, ordering, geometry
        /// calculations, and hit-testing remain owned by BarGraphElement.
        /// </summary>
        private sealed class Unity2022RenderBackend : IBarGraphRenderBackend
        {
            private const int MaxQuadsPerPage = ushort.MaxValue / 4;

            private readonly BarGraphElement _owner;
            private readonly List<MeshPageElement> _pages = new List<MeshPageElement>();
            private LayerElement _underlay;
            private LayerElement _afterBars;
            private LayerElement _foreground;
            private int _activePageCount;
            private int _revision;
            private int _builtRevision = -1;
            private int _observedRequiredPageCount;
            private bool _rootSyncScheduled;
            private bool _capacityCheckScheduled;
            private bool _childrenInvalidatedForPendingRootPaint;

            public Unity2022RenderBackend(BarGraphElement owner) => _owner = owner;

            public void Attach()
            {
                _owner.generateVisualContent += ObserveRootRepaint;
                _underlay = new LayerElement("bar-graph__underlay", DrawUnderlay);
                _afterBars = new LayerElement("bar-graph__after-bars", DrawAfterBars);
                _foreground = new LayerElement("bar-graph__foreground", DrawForeground);

                int labelIndex = _owner.IndexOf(_owner._labelRoot);
                if (labelIndex < 0) labelIndex = _owner.childCount;
                _owner.Insert(labelIndex++, _underlay);
                _owner.Insert(labelIndex++, _afterBars);
                _owner.Insert(labelIndex, _foreground);

                Invalidate();
            }

            public void Invalidate()
            {
                _childrenInvalidatedForPendingRootPaint = true;
                InvalidateChildren();
            }

            private void InvalidateChildren()
            {
                _revision++;
                EnsurePagePool();

                _underlay?.MarkDirtyRepaint();
                _afterBars?.MarkDirtyRepaint();
                _foreground?.MarkDirtyRepaint();
                for (int i = 0; i < _activePageCount; i++)
                    _pages[i].MarkDirtyRepaint();
            }

            public void Detach()
            {
                _owner.generateVisualContent -= ObserveRootRepaint;
                _underlay?.RemoveFromHierarchy();
                _afterBars?.RemoveFromHierarchy();
                _foreground?.RemoveFromHierarchy();
                for (int i = 0; i < _pages.Count; i++)
                    _pages[i].RemoveFromHierarchy();
                _pages.Clear();
                _activePageCount = 0;
            }

            private void ObserveRootRepaint(MeshGenerationContext _)
            {
                // VisualElement.MarkDirtyRepaint() is non-virtual and Unity 2022
                // only dirties the addressed element. A caller holding this chart
                // as VisualElement therefore bypasses BarGraphElement's typed
                // method. Typed invalidations have already dirtied every child and
                // will rebuild from the latest inputs during this paint pass.
                if (_childrenInvalidatedForPendingRootPaint)
                {
                    _childrenInvalidatedForPendingRootPaint = false;
                    ScheduleCapacityCheck();
                    return;
                }

                // A base-typed root-only repaint cannot dirty descendants during
                // paint, so synchronize them at the first legal scheduler tick.
                if (_rootSyncScheduled) return;
                _rootSyncScheduled = true;
                _owner.schedule.Execute(SynchronizeObservedRootRepaint);
            }

            private void ScheduleCapacityCheck()
            {
                if (_capacityCheckScheduled) return;
                _capacityCheckScheduled = true;
                _owner.schedule.Execute(ReconcilePageCapacity);
            }

            private void ReconcilePageCapacity()
            {
                _capacityCheckScheduled = false;
                int required = Math.Max(CalculateRequiredPageCount(), _observedRequiredPageCount);
                if (required != _activePageCount)
                    InvalidateChildren();
            }

            private void SynchronizeObservedRootRepaint()
            {
                _rootSyncScheduled = false;
                InvalidateChildren();
            }

            private void EnsurePagePool()
            {
                int requiredPages = Math.Max(
                    CalculateRequiredPageCount(), _observedRequiredPageCount);

                while (_pages.Count < requiredPages)
                {
                    var page = new MeshPageElement(this, _pages.Count);
                    _pages.Add(page);
                    int insertIndex = _owner.IndexOf(_afterBars);
                    _owner.Insert(insertIndex < 0 ? _owner.childCount : insertIndex, page);
                }

                _activePageCount = requiredPages;
                for (int i = 0; i < _pages.Count; i++)
                    _pages[i].style.display = i < requiredPages
                        ? DisplayStyle.Flex
                        : DisplayStyle.None;
                if (_activePageCount >= _observedRequiredPageCount)
                    _observedRequiredPageCount = 0;
            }

            private int CalculateRequiredPageCount()
            {
                if (!_owner.TryGetRenderLayout(out RenderLayout layout)) return 0;

                long upperBound = EstimateQuadUpperBound(in layout);
                return upperBound <= 0
                    ? 0
                    : (int)Math.Min(int.MaxValue,
                        (upperBound + MaxQuadsPerPage - 1L) / MaxQuadsPerPage);
            }

            private long EstimateQuadUpperBound(in RenderLayout layout)
            {
                int viewCount = layout.EndDisplayIndex - layout.StartDisplayIndex;
                if (viewCount <= 0) return 0;

                if (layout.Stride < 1f)
                {
                    long columns = Math.Max(1, Mathf.FloorToInt(layout.PlotWidth));
                    return _owner._model.HasOverlay ? columns * 2L : columns;
                }

                // Capacity covers either highlight state. With a highlighted tag,
                // matching segments can add one tint quad each and the overlay is
                // suppressed; without a highlight, the overlay can be present.
                // Reserving both upper bounds prevents a coalesced state change
                // from growing the required page count during paint.
                bool mayTintSegments = _owner._vis.TagHighlightTint.a > 0f;
                bool mayDrawOverlay = _owner._model.HasOverlay;
                // DrawDirectBars only emits vertically visible intervals of at
                // least 0.5 px. Its stacked intervals do not overlap, so this is
                // a safe bound after the existing sub-pixel merge pass and avoids
                // allocating pages for raw segments that cannot become quads.
                long maxVisibleSegmentsPerBar = Math.Max(
                    1, Mathf.CeilToInt(layout.PlotHeight * 2f) + 2);
                long total = 0;

                for (int displayIndex = layout.StartDisplayIndex;
                     displayIndex < layout.EndDisplayIndex; displayIndex++)
                {
                    int dataIndex = _owner._viewState.DisplayToData[displayIndex];
                    if (dataIndex < _owner._model.BarCount)
                    {
                        int segmentCount = _owner._model.Bars[dataIndex].SegmentCount;
                        long visibleSegments = Math.Min(segmentCount, maxVisibleSegmentsPerBar);
                        total += mayTintSegments ? visibleSegments * 2L : visibleSegments;
                    }

                    if (mayDrawOverlay && dataIndex < _owner._model.OverlayBarCount)
                    {
                        int segmentCount = _owner._model.OverlayBars[dataIndex].SegmentCount;
                        total += Math.Min(segmentCount, maxVisibleSegmentsPerBar);
                    }
                }

                return total;
            }

            private void EnsureGeometry()
            {
                if (_builtRevision == _revision) return;

                if (_owner.TryGetRenderLayout(out RenderLayout layout))
                    _owner.BuildBarGeometry(in layout);
                else
                    _owner._quadCount = 0;

                _builtRevision = _revision;

                int required = _owner._quadCount <= 0
                    ? 0
                    : (_owner._quadCount + MaxQuadsPerPage - 1) / MaxQuadsPerPage;
                if (required > _activePageCount)
                {
                    _observedRequiredPageCount = required;
                    Debug.LogWarning(
                        $"BarGraphElement render backend underestimated mesh pages " +
                        $"({_activePageCount} active, {required} required). " +
                        $"The page pool will be reconciled on the next UI update.");
                    ScheduleCapacityCheck();
                }
            }

            private void DrawUnderlay(MeshGenerationContext mgc)
            {
                if (_owner.TryGetContentRect(out Rect content))
                    _owner.DrawBackground(mgc, content);

                if (_owner.TryGetRenderLayout(out RenderLayout layout))
                    _owner.DrawUnderlay(mgc, in layout);
            }

            private void DrawAfterBars(MeshGenerationContext mgc)
            {
                if (_owner.TryGetRenderLayout(out RenderLayout layout))
                    _owner.DrawAfterBars(mgc, in layout);
            }

            private void DrawForeground(MeshGenerationContext mgc)
            {
                if (_owner.TryGetRenderLayout(out RenderLayout layout))
                    _owner.DrawForeground(mgc, in layout);
            }

            private void DrawPage(MeshGenerationContext mgc, int pageIndex)
            {
                EnsureGeometry();
                int offset = pageIndex * MaxQuadsPerPage;
                if (offset >= _owner._quadCount) return;

                int count = Math.Min(MaxQuadsPerPage, _owner._quadCount - offset);
                _owner.FlushQuadRange(mgc, offset, count);
            }

            private sealed class LayerElement : VisualElement
            {
                public LayerElement(string elementName, Action<MeshGenerationContext> draw)
                {
                    name = elementName;
                    pickingMode = PickingMode.Ignore;
                    focusable = false;
                    SetFullSizeAbsolute(this);
                    generateVisualContent += draw;
                }
            }

            private sealed class MeshPageElement : VisualElement
            {
                public MeshPageElement(Unity2022RenderBackend backend, int pageIndex)
                {
                    name = $"bar-graph__mesh-page-{pageIndex}";
                    pickingMode = PickingMode.Ignore;
                    focusable = false;
                    SetFullSizeAbsolute(this);
                    generateVisualContent += mgc => backend.DrawPage(mgc, pageIndex);
                }
            }

            private static void SetFullSizeAbsolute(VisualElement element)
            {
                element.style.position = Position.Absolute;
                element.style.left = 0;
                element.style.top = 0;
                element.style.right = 0;
                element.style.bottom = 0;
            }
        }
#endif
    }
}
