using System;
using System.Collections.Generic;
using UnityEngine;

namespace GCAllocTest.DataStructures
{
    /// <summary>
    /// Tests a range of allocation sizes from tiny (16B) to large (256KB+),
    /// nested class structures, and generic method allocations.
    /// Useful for verifying byte formatting, sorting, and size distribution.
    /// </summary>
    public class VariedSizeAllocator : MonoBehaviour
    {
        [Header("Size Spectrum")]
        [Tooltip("Tiny: 16-byte arrays")]
        public bool tinyAllocs = true;

        [Tooltip("Small: 256-byte arrays")]
        public bool smallAllocs = true;

        [Tooltip("Medium: 4 KB arrays")]
        public bool mediumAllocs = true;

        [Tooltip("Large: 64 KB arrays")]
        public bool largeAllocs = true;

        [Tooltip("Huge: 256 KB arrays (LOH threshold)")]
        public bool hugeAllocs = true;
        public int hugeAllocInterval = 120;

        [Header("Nested Class Patterns")]
        [Tooltip("Allocate inner class instances")]
        public bool nestedClassAllocs = true;

        [Header("Generic Allocations")]
        [Tooltip("Generic method allocations")]
        public bool genericAllocs = true;

        int m_Frame;

        void Update()
        {
            m_Frame++;

            if (tinyAllocs) AllocTiny();
            if (smallAllocs) AllocSmall();
            if (mediumAllocs) AllocMedium();
            if (largeAllocs && m_Frame % 10 == 0) AllocLarge();
            if (hugeAllocs && m_Frame % hugeAllocInterval == 0) AllocHuge();
            if (nestedClassAllocs) AllocNestedClasses();
            if (genericAllocs) AllocGenerics();
        }

        // ── Size spectrum ───────────────────────────────────

        void AllocTiny()
        {
            // 16 bytes — below typical small object threshold
            byte[] tiny = new byte[16];
        }

        void AllocSmall()
        {
            byte[] small = new byte[256];
        }

        void AllocMedium()
        {
            byte[] medium = new byte[4096];
        }

        void AllocLarge()
        {
            byte[] large = new byte[65536]; // 64 KB
        }

        void AllocHuge()
        {
            // 256 KB — goes on LOH in .NET
            byte[] huge = new byte[262144];
        }

        // ── Nested classes ──────────────────────────────────

        void AllocNestedClasses()
        {
            // Tests that nested class names display correctly in the analyzer
            var node = new DataNode
            {
                Name = "Node_" + m_Frame,
                Value = m_Frame * 0.1f,
                Children = new List<DataNode.ChildEntry>(2)
                {
                    new DataNode.ChildEntry { Index = 0, Weight = 1.0f },
                    new DataNode.ChildEntry { Index = 1, Weight = 0.5f }
                }
            };

            // Double-nested
            var config = new DataNode.ChildEntry.Metadata
            {
                Tag = "test",
                Flags = new int[] { 1, 2, 3 }
            };
        }

        // ── Generic methods ─────────────────────────────────

        void AllocGenerics()
        {
            // Generic instantiations — each distinct T may cause different call stack
            CreateWrapper<int>(42);
            CreateWrapper<string>("hello");
            CreateWrapper<Vector3>(Vector3.zero);
            CreatePair(1, "one");
            CreatePair(2, "two");
        }

        static Wrapper<T> CreateWrapper<T>(T value)
        {
            return new Wrapper<T> { Value = value };
        }

        static KeyValuePair<TKey, TValue> CreatePair<TKey, TValue>(TKey key, TValue value)
        {
            // The KVP is a struct (no alloc), but this list allocation is real
            var list = new List<KeyValuePair<TKey, TValue>>(1)
            {
                new KeyValuePair<TKey, TValue>(key, value)
            };
            return list[0];
        }

        // ── Nested types ────────────────────────────────────

        [Serializable]
        public class DataNode
        {
            public string Name;
            public float Value;
            public List<ChildEntry> Children;

            [Serializable]
            public class ChildEntry
            {
                public int Index;
                public float Weight;

                public class Metadata
                {
                    public string Tag;
                    public int[] Flags;
                }
            }
        }

        public class Wrapper<T>
        {
            public T Value;
        }
    }
}
