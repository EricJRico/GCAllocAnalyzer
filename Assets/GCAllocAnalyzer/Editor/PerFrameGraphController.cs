using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace GCAllocBreakdown.Editor
{
    // ═══════════════════════════════════════════════════
    //  PER-FRAME GRAPH CONTROLLER
    //  Extracts the per-frame bar graph from GCAllocAnalyzerWindow
    //  into a standalone controller that builds, rebuilds, and
    //  overlays the graph UI. The main window creates an instance
    //  and delegates all graph operations to it.
    // ═══════════════════════════════════════════════════

    internal class PerFrameGraphController
    {
        // ═══════════════════════════════════════════════════
        //  CONSTANTS
        // ═══════════════════════════════════════════════════

        const float GRAPH_HEIGHT = 120;
        const float GRAPH_Y_AXIS_WIDTH = 52;
        const float GRAPH_X_AXIS_HEIGHT = 16;

        // ═══════════════════════════════════════════════════
        //  COLORS
        // ═══════════════════════════════════════════════════

        static readonly Color k_GraphBg = new(0.1f, 0.1f, 0.1f);
        static readonly Color k_GraphGuideLine = new(0.2f, 0.2f, 0.2f);
        static readonly Color k_GraphBar = new(0.27f, 0.67f, 0.6f);          // teal
        static readonly Color k_GraphOverlay = new(1f, 1f, 1f, 0.85f);       // bright white
        static readonly Color k_DimGray = new(0.7f, 0.7f, 0.7f);

        // ═══════════════════════════════════════════════════
        //  CALLBACKS
        // ═══════════════════════════════════════════════════

        readonly Action<int> m_OnFrameSelected;
        readonly Func<int> m_GetSelectedMarkerIndex;

        // ═══════════════════════════════════════════════════
        //  UI ELEMENTS
        // ═══════════════════════════════════════════════════

        Foldout m_GraphFoldout;
        VisualElement m_GraphRoot;
        VisualElement m_GraphBarArea;
        Label m_GraphYMax, m_GraphYMid;
        Label m_GraphXStart, m_GraphXEnd;
        Label m_GraphOverlayLabel;

        // ═══════════════════════════════════════════════════
        //  DATA REFERENCES (set on each analysis)
        // ═══════════════════════════════════════════════════

        AnalysisSnapshot m_Snapshot;
        List<CallsiteGroup> m_FilteredGroups;

        // ═══════════════════════════════════════════════════
        //  BUCKETING BUFFERS (owned by this controller)
        // ═══════════════════════════════════════════════════

        long[] m_GraphBuckets;
        long[] m_GraphOverlayBuckets;
        int m_GraphBucketCount;
        int m_GraphFramesPerBucket;

        // Per-frame scratch buffer (owned by this controller, separate from main window's)
        long[] m_PerFrameBuffer;

        // ═══════════════════════════════════════════════════
        //  PUBLIC API
        // ═══════════════════════════════════════════════════

        /// <summary>The root Foldout element — add this to the parent layout.</summary>
        public VisualElement Root => m_GraphFoldout;

        public PerFrameGraphController(Action<int> onFrameSelected, Func<int> getSelectedMarkerIndex)
        {
            m_OnFrameSelected = onFrameSelected;
            m_GetSelectedMarkerIndex = getSelectedMarkerIndex;

            BuildUI();
        }

        /// <summary>
        /// Store data references after each analysis run.
        /// </summary>
        public void SetData(AnalysisSnapshot snapshot, List<CallsiteGroup> filteredGroups)
        {
            m_Snapshot = snapshot;
            m_FilteredGroups = filteredGroups;
        }

        /// <summary>
        /// Rebuild the entire graph from current snapshot data.
        /// </summary>
        public void RebuildGraph()
        {
            if (m_Snapshot == null)
            {
                m_GraphFoldout.style.display = DisplayStyle.None;
                return;
            }

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
            m_GraphFramesPerBucket = 1;
            if (frameCount > (int)areaWidth)
                m_GraphFramesPerBucket = Mathf.CeilToInt((float)frameCount / Mathf.Max(1f, areaWidth));
            // Derive bucket count from framesPerBucket so no trailing empty buckets
            m_GraphBucketCount = Mathf.CeilToInt((float)frameCount / m_GraphFramesPerBucket);

            // Ensure bucket buffer
            if (m_GraphBuckets == null || m_GraphBuckets.Length < m_GraphBucketCount)
                m_GraphBuckets = new long[m_GraphBucketCount];
            else
                Array.Clear(m_GraphBuckets, 0, m_GraphBucketCount);

            // Fill buckets (max of frames in each bucket)
            long maxValue = 0;
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
                if (bucketMax > maxValue) maxValue = bucketMax;
            }

            // Update Y-axis labels
            m_GraphYMax.text = GCAllocUtils.FormatBytes(maxValue);
            m_GraphYMid.text = GCAllocUtils.FormatBytes(maxValue / 2);

            // Update X-axis labels
            m_GraphXStart.text = m_Snapshot.FrameStart.ToString();
            m_GraphXEnd.text = m_Snapshot.FrameEnd.ToString();

            // Clear old bars
            m_GraphBarArea.Clear();

            float barWidth = areaWidth / m_GraphBucketCount;
            float maxHeight = GRAPH_HEIGHT;

            // Add horizontal guide line (mid-point)
            var guideMid = new VisualElement
            {
                name = "guideline",
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

                // Outer bar element
                var bar = new VisualElement
                {
                    style =
                    {
                        width = Mathf.Max(barWidth, 1f),
                        height = Mathf.Max(height, val > 0 ? 3f : 0f),
                        backgroundColor = k_GraphBar,
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
                    bar.tooltip = string.Concat("Frame ", GCAllocUtils.DisplayFrame(frameStart).ToString(), ": ", GCAllocUtils.FormatBytes(val));
                else
                    bar.tooltip = string.Concat("Frames ", GCAllocUtils.DisplayFrame(frameStart).ToString(), "\u2013", GCAllocUtils.DisplayFrame(frameEnd).ToString(), ": ", GCAllocUtils.FormatBytes(val), " (max)");

                // Click handler — userData-based, no closures
                bar.RegisterCallback<ClickEvent>(OnGraphBarClicked);

                m_GraphBarArea.Add(bar);
            }

            // Re-apply overlay for currently selected marker (if any)
            int selectedIdx = m_GetSelectedMarkerIndex != null ? m_GetSelectedMarkerIndex() : -1;
            if (m_FilteredGroups != null &&
                selectedIdx >= 0 &&
                selectedIdx < m_FilteredGroups.Count)
                UpdateOverlay(m_FilteredGroups[selectedIdx]);
            else
                m_GraphOverlayLabel.text = "";
        }

        /// <summary>
        /// Show overlay bars for a specific callsite group.
        /// </summary>
        public void UpdateOverlay(CallsiteGroup group)
        {
            if (group == null)
            {
                ClearOverlay();
                return;
            }

            if (m_Snapshot == null || m_Snapshot.PerFrameBytes == null) return;

            int frameCount = m_Snapshot.PerFrameBytes.Length;

            // Ensure overlay bucket buffer
            if (m_GraphOverlayBuckets == null || m_GraphOverlayBuckets.Length < m_GraphBucketCount)
                m_GraphOverlayBuckets = new long[m_GraphBucketCount];
            else
                Array.Clear(m_GraphOverlayBuckets, 0, m_GraphBucketCount);

            // Build per-frame bytes for this group
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

            // Find max from main graph buckets for proportional overlay height
            long maxValue = 0;
            for (int b = 0; b < m_GraphBucketCount; b++)
                if (m_GraphBuckets[b] > maxValue) maxValue = m_GraphBuckets[b];

            // Update overlay elements on existing bars
            int b2 = 0;
            for (int i = 0; i < m_GraphBarArea.childCount && b2 < m_GraphBucketCount; i++)
            {
                var child = m_GraphBarArea[i];
                if (child.name == "guideline") continue;

                var overlay = child.Q("overlay");
                if (overlay == null) { b2++; continue; }

                long overlayVal = m_GraphOverlayBuckets[b2];
                if (overlayVal <= 0 || maxValue <= 0)
                {
                    overlay.style.height = 0;
                    b2++;
                    continue;
                }

                float overlayHeight = (float)overlayVal / maxValue * GRAPH_HEIGHT;
                overlay.style.height = Mathf.Max(overlayHeight, 1f);
                b2++;
            }

            // Update overlay label
            m_GraphOverlayLabel.text = group.DisplayName;
        }

        /// <summary>
        /// Clear all overlay bars and the overlay label.
        /// </summary>
        public void ClearOverlay()
        {
            for (int i = 0; i < m_GraphBarArea.childCount; i++)
            {
                var child = m_GraphBarArea[i];
                if (child.name == "guideline") continue;
                var overlay = child.Q("overlay");
                if (overlay != null) overlay.style.height = 0;
            }
            m_GraphOverlayLabel.text = "";
        }

        // ═══════════════════════════════════════════════════
        //  UI CONSTRUCTION
        // ═══════════════════════════════════════════════════

        void BuildUI()
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
            m_GraphYMax = new Label("\u2014") { style = { fontSize = 10, color = k_DimGray, unityTextAlign = TextAnchor.MiddleRight } };
            m_GraphYMid = new Label("\u2014") { style = { fontSize = 10, color = k_DimGray, unityTextAlign = TextAnchor.MiddleRight } };
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
            m_GraphXStart = new Label("\u2014") { style = { fontSize = 10, color = k_DimGray, flexShrink = 0 } };
            m_GraphOverlayLabel = new Label("")
            {
                style =
                {
                    flexGrow = 1,
                    flexShrink = 1,
                    fontSize = 10,
                    color = k_DimGray,
                    unityTextAlign = TextAnchor.MiddleCenter,
                    overflow = Overflow.Hidden,
                    textOverflow = TextOverflow.Ellipsis,
                    whiteSpace = WhiteSpace.NoWrap,
                    marginLeft = 8,
                    marginRight = 8
                }
            };
            m_GraphXEnd = new Label("\u2014") { style = { fontSize = 10, color = k_DimGray, flexShrink = 0 } };
            xAxis.Add(m_GraphXStart);
            xAxis.Add(m_GraphOverlayLabel);
            xAxis.Add(m_GraphXEnd);
            chartColumn.Add(xAxis);

            m_GraphRoot.Add(chartColumn);

            m_GraphFoldout.Add(m_GraphRoot);
        }

        // ═══════════════════════════════════════════════════
        //  EVENT HANDLERS
        // ═══════════════════════════════════════════════════

        void OnGraphGeometryChanged(GeometryChangedEvent evt)
        {
            if (m_Snapshot == null || !m_Snapshot.HasData || m_Snapshot.PerFrameBytes == null) return;
            // Only rebuild if width actually changed meaningfully (>2px)
            float oldW = evt.oldRect.width;
            float newW = evt.newRect.width;
            if (Mathf.Abs(newW - oldW) < 2f) return;
            RebuildGraph();
        }

        void OnGraphBarClicked(ClickEvent evt)
        {
            var bar = evt.currentTarget as VisualElement;
            if (bar?.userData is not int bucketIdx) return;

            if (m_Snapshot == null) return;

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

            // Notify the main window so it can jump the Profiler to this frame
            if (m_OnFrameSelected != null)
                m_OnFrameSelected(frameIndex);
        }

        // ═══════════════════════════════════════════════════
        //  PRIVATE HELPERS
        // ═══════════════════════════════════════════════════

        void EnsurePerFrameBuffer(int frameCount)
        {
            if (m_PerFrameBuffer == null || m_PerFrameBuffer.Length < frameCount)
                m_PerFrameBuffer = new long[frameCount];
            else
                Array.Clear(m_PerFrameBuffer, 0, frameCount);
        }

        /// <summary>
        /// Creates a foldout styled to match Unity's Profile Analyzer:
        /// bold header text, subtle background on the header bar, separator line.
        /// </summary>
        static Foldout MakeSectionFoldout(string title, bool defaultOpen = true)
        {
            var foldout = new Foldout { text = title, value = defaultOpen };
            foldout.style.marginBottom = 2;
            foldout.style.marginTop = 2;

            // Style the toggle (header bar) — subtle background like Profile Analyzer
            var toggle = foldout.Q<Toggle>();
            if (toggle != null)
            {
                toggle.style.backgroundColor = new Color(0.25f, 0.25f, 0.25f);
                toggle.style.paddingTop = 3;
                toggle.style.paddingBottom = 3;
                toggle.style.paddingLeft = 2;
                toggle.style.marginBottom = 2;
                toggle.style.borderBottomWidth = 1;
                toggle.style.borderBottomColor = new Color(0.15f, 0.15f, 0.15f);

                var label = toggle.Q<Label>();
                if (label != null)
                {
                    label.style.fontSize = 12;
                    label.style.unityFontStyleAndWeight = FontStyle.Bold;
                }
            }

            return foldout;
        }
    }
}
