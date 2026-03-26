using System;
using System.Collections.Generic;
using UnityEngine;

namespace GCAllocBreakdown.BarChart.Core
{
    /// <summary>
    /// Holds the canonical bar + segment data.
    ///
    /// Memory model
    /// ─────────────
    ///  • Two pre-allocated backing arrays (<see cref="_bars"/>, <see cref="_segments"/>)
    ///    grow via doubling but never shrink.  <see cref="SetData"/> copies into these
    ///    arrays – no heap allocation after the initial warm-up.
    ///  • A separate overlay series is stored the same way.
    ///  • <see cref="MaxPrimaryY"/> and <see cref="MaxOverlayY"/> are recomputed
    ///    whenever data changes.
    /// </summary>
    public sealed class ChartDataModel
    {
        // ── Primary series ─────────────────────────────────────────────────────
        private BarEntry[]   _bars     = new BarEntry[64];
        private BarSegment[] _segments = new BarSegment[256];
        private int _barCount;
        private int _segmentCount;

        // ── Overlay series ─────────────────────────────────────────────────────
        private BarEntry[]   _overlayBars     = Array.Empty<BarEntry>();
        private BarSegment[] _overlaySegments = Array.Empty<BarSegment>();
        private int _overlayBarCount;
        private int _overlaySegmentCount;

        // ── Derived / cached ───────────────────────────────────────────────────
        public float MaxPrimaryY  { get; private set; } = 1f;
        public float MaxOverlayY  { get; private set; } = 1f;
        public bool  HasOverlay   => _overlayBarCount > 0;

        // ── Events ─────────────────────────────────────────────────────────────
        /// <summary>Raised after primary data mutation. Fires on the calling thread.</summary>
        public event Action DataChanged;
        /// <summary>Raised after overlay data mutation. Fires on the calling thread.</summary>
        public event Action OverlayChanged;

        // ── Read accessors (no allocation) ─────────────────────────────────────
        public int BarCount      => _barCount;
        public int SegmentCount  => _segmentCount;

        /// <summary>Read-only view of the primary bars array (count: <see cref="BarCount"/>).</summary>
        public BarEntry[]   Bars     => _bars;      // Callers must not write past [BarCount-1]
        /// <summary>Read-only view of the primary segments array.</summary>
        public BarSegment[] Segments => _segments;

        public int OverlayBarCount => _overlayBarCount;
        public BarEntry[]   OverlayBars     => _overlayBars;
        public BarSegment[] OverlaySegments => _overlaySegments;

        // ─────────────────────────────────────────────────────────────────────
        //  Primary series – SetData overloads
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Full stacked-bar data.  Both arrays are copied into pre-allocated storage.
        /// </summary>
        public void SetData(BarEntry[] bars, int barCount, BarSegment[] segments, int segCount)
        {
            EnsureCapacity(ref _bars,     barCount);
            EnsureCapacity(ref _segments, segCount);
            Array.Copy(bars,     _bars,     barCount);
            Array.Copy(segments, _segments, segCount);
            _barCount     = barCount;
            _segmentCount = segCount;
            RecalcPrimaryBounds();
            DataChanged?.Invoke();
        }

        /// <summary>Convenience: single-segment bars from a flat BarEntry list.</summary>
        public void SetData(IList<BarEntry> bars)
        {
            int n = bars.Count;
            EnsureCapacity(ref _bars,     n);
            EnsureCapacity(ref _segments, n);
            for (int i = 0; i < n; i++)
            {
                _segments[i] = new BarSegment(bars[i].TotalValue);   // segment placeholder
                _bars[i]     = new BarEntry(i, 1, bars[i].TotalValue, bars[i].Label);
            }
            _barCount     = n;
            _segmentCount = n;
            RecalcPrimaryBounds();
            DataChanged?.Invoke();
        }

        /// <summary>Convenience: flat float values, single colour per bar.</summary>
        public void SetData(IList<float> values, Color32 color = default)
        {
            int n = values.Count;
            EnsureCapacity(ref _bars,     n);
            EnsureCapacity(ref _segments, n);
            for (int i = 0; i < n; i++)
            {
                float v      = Mathf.Max(0f, values[i]);
                _segments[i] = new BarSegment(v, color);
                _bars[i]     = new BarEntry(i, 1, v);
            }
            _barCount     = n;
            _segmentCount = n;
            RecalcPrimaryBounds();
            DataChanged?.Invoke();
        }

        /// <summary>Append one single-segment bar (O(1) amortised, no rebuild).</summary>
        public void AppendBar(float value, Color32 color = default, string label = null)
        {
            EnsureCapacity(ref _bars,     _barCount     + 1);
            EnsureCapacity(ref _segments, _segmentCount + 1);

            _segments[_segmentCount] = new BarSegment(value, color);
            _bars[_barCount]         = new BarEntry(_segmentCount, 1, value, label);
            _barCount++;
            _segmentCount++;

            if (value > MaxPrimaryY) MaxPrimaryY = value;
            DataChanged?.Invoke();
        }

        /// <summary>Remove all bars and segments from the primary series.</summary>
        public void Clear()
        {
            _barCount     = 0;
            _segmentCount = 0;
            MaxPrimaryY   = 1f;
            DataChanged?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Overlay series
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Set the secondary / comparison overlay dataset.</summary>
        public void SetOverlay(BarEntry[] bars, int barCount, BarSegment[] segments, int segCount)
        {
            EnsureCapacity(ref _overlayBars,     barCount);
            EnsureCapacity(ref _overlaySegments, segCount);
            Array.Copy(bars,     _overlayBars,     barCount);
            Array.Copy(segments, _overlaySegments, segCount);
            _overlayBarCount     = barCount;
            _overlaySegmentCount = segCount;
            RecalcOverlayBounds();
            OverlayChanged?.Invoke();
        }

        /// <summary>Convenience flat-float overlay.</summary>
        public void SetOverlay(IList<float> values, Color32 color = default)
        {
            int n = values.Count;
            EnsureCapacity(ref _overlayBars,     n);
            EnsureCapacity(ref _overlaySegments, n);
            for (int i = 0; i < n; i++)
            {
                float v              = Mathf.Max(0f, values[i]);
                _overlaySegments[i]  = new BarSegment(v, color);
                _overlayBars[i]      = new BarEntry(i, 1, v);
            }
            _overlayBarCount     = n;
            _overlaySegmentCount = n;
            RecalcOverlayBounds();
            OverlayChanged?.Invoke();
        }

        /// <summary>Remove the overlay series.</summary>
        public void ClearOverlay()
        {
            _overlayBarCount     = 0;
            _overlaySegmentCount = 0;
            MaxOverlayY          = 1f;
            OverlayChanged?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────

        private void RecalcPrimaryBounds()
        {
            float max = 0f;
            for (int i = 0; i < _barCount; i++)
                if (_bars[i].TotalValue > max) max = _bars[i].TotalValue;
            MaxPrimaryY = max > 0f ? max : 1f;
        }

        private void RecalcOverlayBounds()
        {
            float max = 0f;
            for (int i = 0; i < _overlayBarCount; i++)
                if (_overlayBars[i].TotalValue > max) max = _overlayBars[i].TotalValue;
            MaxOverlayY = max > 0f ? max : 1f;
        }

        private static void EnsureCapacity<T>(ref T[] arr, int needed)
        {
            if (arr.Length < needed)
                Array.Resize(ref arr, Math.Max(arr.Length * 2, needed));
        }
    }
}
