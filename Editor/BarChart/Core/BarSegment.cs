using System;
using UnityEngine;

namespace GCAllocBreakdown.BarChart.Core
{
    /// <summary>
    /// One coloured slice inside a stacked bar.
    /// A bar with a single segment behaves identically to a flat bar.
    /// </summary>
    [Serializable]
    public readonly struct BarSegment
    {
        /// <summary>Magnitude of this segment (always positive).</summary>
        public readonly float Value;

        /// <summary>
        /// Fill colour.  default(Color32) defers to the <c>--bar-graph-default-bar-color</c> USS custom property.
        /// </summary>
        public readonly Color32 Color;

        /// <summary>
        /// User-defined identity tag.  Typically a shared ID representing a
        /// category that appears across many bars (e.g. an allocation site ID).
        /// The graph never interprets this value — it carries it through the
        /// pipeline and returns it in interaction events.
        /// <para>Default <c>-1</c> means anonymous / no identity.</para>
        /// </summary>
        public readonly int Tag;

        public BarSegment(float value, Color32 color, int tag = -1)
        {
            Value = Mathf.Max(0f, value);
            Color = color;
            Tag   = tag;
        }

        public BarSegment(float value, Color color, int tag = -1)
            : this(value, (Color32)color, tag) { }

        // Explicit default(Color32) — 'default' alone is ambiguous between
        // the Color32 and Color overloads in older C# language versions.
        public BarSegment(float value)
            : this(value, default(Color32), -1) { }
    }
}
