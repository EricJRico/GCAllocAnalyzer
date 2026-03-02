using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GCAllocTest.DeepStacks
{
    // ═══════════════════════════════════════════════════════════════════════════
    // DeepCallstackAllocator
    // Generates GC allocations at configurable call depths (10-30+) to test
    // callstack resolution in the GC Alloc Analyzer.
    // ═══════════════════════════════════════════════════════════════════════════

    public class DeepCallstackAllocator : MonoBehaviour
    {
        // ═══════════════════════════════════════════════════════════════════════
        // Inspector Fields
        // ═══════════════════════════════════════════════════════════════════════

        [Header("Patterns")]
        [SerializeField] bool m_EnableLinearChain = true;
        [SerializeField] bool m_EnableRecursive = true;
        [SerializeField] bool m_EnableMixed = true;

        [Header("Configuration")]
        [SerializeField] int m_RecursionDepth = 12;

        // ═══════════════════════════════════════════════════════════════════════
        // Private State
        // ═══════════════════════════════════════════════════════════════════════

        int m_Frame;
        StringBuilder m_SharedSB;

        // ═══════════════════════════════════════════════════════════════════════
        // MonoBehaviour
        // ═══════════════════════════════════════════════════════════════════════

        void Awake()
        {
            m_SharedSB = new StringBuilder(256);
        }

        void Update()
        {
            m_Frame++;

            // Pattern 1: Linear chain — every frame
            if (m_EnableLinearChain)
            {
                m_SharedSB.Clear();
                ServiceA.Step01(m_SharedSB);
            }

            // Pattern 2: Recursive — every 5 frames
            if (m_EnableRecursive && m_Frame % 5 == 0)
            {
                RecursiveAlloc(m_RecursionDepth, "R");
            }

            // Pattern 3: Mixed — every 15 frames
            if (m_EnableMixed && m_Frame % 15 == 0)
            {
                HandleEvent();
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Pattern 2: Recursive
        // ═══════════════════════════════════════════════════════════════════════

        void RecursiveAlloc(int depth, string prefix)
        {
            // Allocate at every level: string concatenation
            string current = prefix + "_L" + depth.ToString();
            _ = current;

            if (depth == 0)
            {
                // Leaf: additional allocations
                _ = new List<float>(m_RecursionDepth);
                _ = new byte[64];
                return;
            }

            RecursiveAlloc(depth - 1, current);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Pattern 3: Mixed — instance methods
        // ═══════════════════════════════════════════════════════════════════════

        void HandleEvent()
        {
            EventDispatcher.Dispatch(this);
        }

        public void ProcessPayload()
        {
            Serializer.Serialize();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Pattern 1: Linear Chain — Static Helper Classes
    // 18-level chain: ServiceA (01-06) → ServiceB (07-12) → ServiceC (13-18)
    // Only the leaf (Step18) allocates. Intermediate steps just pass through.
    // ═══════════════════════════════════════════════════════════════════════════

    static class ServiceA
    {
        public static void Step01(StringBuilder sb) { ServiceA.Step02(sb); }
        public static void Step02(StringBuilder sb) { ServiceA.Step03(sb); }
        public static void Step03(StringBuilder sb) { ServiceA.Step04(sb); }
        public static void Step04(StringBuilder sb) { ServiceA.Step05(sb); }
        public static void Step05(StringBuilder sb) { ServiceA.Step06(sb); }
        public static void Step06(StringBuilder sb) { ServiceB.Step07(sb); }
    }

    static class ServiceB
    {
        public static void Step07(StringBuilder sb) { ServiceB.Step08(sb); }
        public static void Step08(StringBuilder sb) { ServiceB.Step09(sb); }
        public static void Step09(StringBuilder sb) { ServiceB.Step10(sb); }
        public static void Step10(StringBuilder sb) { ServiceB.Step11(sb); }
        public static void Step11(StringBuilder sb) { ServiceB.Step12(sb); }
        public static void Step12(StringBuilder sb) { ServiceC.Step13(sb); }
    }

    static class ServiceC
    {
        public static void Step13(StringBuilder sb) { ServiceC.Step14(sb); }
        public static void Step14(StringBuilder sb) { ServiceC.Step15(sb); }
        public static void Step15(StringBuilder sb) { ServiceC.Step16(sb); }
        public static void Step16(StringBuilder sb) { ServiceC.Step17(sb); }
        public static void Step17(StringBuilder sb) { ServiceC.Step18(sb); }

        public static void Step18(StringBuilder sb)
        {
            // Leaf: allocate here so the full 18-level callstack is visible
            _ = new byte[128];
            sb.Append("DeepChainLeaf");
            _ = sb.ToString();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Pattern 3: Mixed — Static Helper Classes
    // Alternates instance and static methods with a recursive segment.
    // ~10-12 levels total depending on TreeWalker depth.
    // ═══════════════════════════════════════════════════════════════════════════

    static class EventDispatcher
    {
        public static void Dispatch(DeepCallstackAllocator target)
        {
            target.ProcessPayload();
        }
    }

    static class Serializer
    {
        public static void Serialize()
        {
            WriteFields();
        }

        public static void WriteFields()
        {
            TreeWalker.Walk("root", 4);
        }
    }

    static class TreeWalker
    {
        public static void Walk(string node, int depth)
        {
            // Allocate a small string at every level
            string label = node + ".child_" + depth.ToString();
            _ = label;

            if (depth <= 0)
            {
                // Leaf: larger allocations
                _ = "TreeWalker_leaf_" + node;
                _ = new byte[96];
                return;
            }

            Walk(label, depth - 1);
        }
    }
}
