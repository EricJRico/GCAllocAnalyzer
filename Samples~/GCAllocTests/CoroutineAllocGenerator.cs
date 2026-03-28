using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GCAllocTest
{
    /// <summary>
    /// Coroutine-based allocations — a very common source of GC allocs in Unity.
    /// Each coroutine start allocates an enumerator. yield return new WaitForSeconds
    /// allocates each time. StartCoroutine itself allocates a Coroutine object.
    /// </summary>
    public class CoroutineAllocGenerator : MonoBehaviour
    {
        [Header("Coroutine Patterns")]
        [Tooltip("Start a new coroutine every N frames")]
        public bool startCoroutineEveryN = true;
        public int coroutineInterval = 30;

        [Tooltip("yield return new WaitForSeconds (allocates each yield)")]
        public bool yieldNewWait = true;

        [Tooltip("yield return new WaitForEndOfFrame")]
        public bool yieldEndOfFrame = true;

        [Tooltip("yield return string (boxes value types)")]
        public bool yieldBoxedValue = true;

        [Header("Nested Coroutines")]
        [Tooltip("Coroutine that starts sub-coroutines")]
        public bool nestedCoroutines = true;
        public int nestingDepth = 3;

        int m_Frame;
        WaitForSeconds m_CachedWait; // Demonstrate the fix pattern
        bool m_Optimized;
        StringBuilder m_SharedSB;
        WaitForSeconds m_CachedWait05;
        WaitForEndOfFrame m_CachedEOF;
        int m_ShortLivedCountdown;

        void Start()
        {
            m_CachedWait = new WaitForSeconds(0.1f);
            m_Optimized = GCAllocTestRig.CurrentMode == AllocMode.Optimized;
            m_SharedSB = new StringBuilder(128);
            m_CachedWait05 = new WaitForSeconds(0.5f);
            m_CachedEOF = new WaitForEndOfFrame();

            if (yieldNewWait)
                StartCoroutine(m_Optimized ? OptimizedWaitLoop() : AllocatingWaitLoop());
            if (yieldEndOfFrame)
                StartCoroutine(m_Optimized ? OptimizedEndOfFrameLoop() : EndOfFrameLoop());
            if (yieldBoxedValue)
                StartCoroutine(m_Optimized ? OptimizedBoxedYieldLoop() : BoxedYieldLoop());
        }

        void Update()
        {
            m_Frame++;

            if (startCoroutineEveryN && m_Frame % coroutineInterval == 0)
            {
                if (m_Optimized)
                {
                    // Manual frame counter — no coroutine start, no enumerator allocation
                    m_SharedSB.Clear();
                    m_SharedSB.Append("ShortLived_").Append(m_Frame);
                    m_ShortLivedCountdown = 1;
                }
                else
                {
                    StartCoroutine(ShortLivedCoroutine());
                }
            }

            // Tick down the manual countdown
            if (m_Optimized && m_ShortLivedCountdown > 0)
                m_ShortLivedCountdown--;

            if (nestedCoroutines && m_Frame % (coroutineInterval * 2) == 0)
            {
                if (m_Optimized)
                    StartCoroutine(OptimizedFlatCoroutine(nestingDepth));
                else
                    StartCoroutine(OuterCoroutine(nestingDepth));
            }
        }

        // ══════════════════════════════════════════════════════
        // ── Yield allocations (baseline) ─────────────────────
        // ══════════════════════════════════════════════════════

        IEnumerator AllocatingWaitLoop()
        {
            while (true)
            {
                // BAD: allocates a new WaitForSeconds every iteration
                yield return new WaitForSeconds(0.5f);

                // Some work that also allocates
                string msg = "Waited at frame " + Time.frameCount;
            }
        }

        IEnumerator EndOfFrameLoop()
        {
            while (true)
            {
                // Allocates a new WaitForEndOfFrame each time
                yield return new WaitForEndOfFrame();
            }
        }

        IEnumerator BoxedYieldLoop()
        {
            int counter = 0;
            while (true)
            {
                counter++;
                // yield return with a value type — boxes it
                yield return null; // null is fine, no alloc
                yield return counter; // boxes the int!

                if (counter > 10000) counter = 0;
            }
        }

        // ══════════════════════════════════════════════════════
        // ── Short-lived coroutine (baseline) ─────────────────
        // ══════════════════════════════════════════════════════

        IEnumerator ShortLivedCoroutine()
        {
            // Enumerator + Coroutine allocated on start
            string label = "ShortLived_" + m_Frame;
            yield return null;
            // Done — but the allocations already happened
        }

        // ══════════════════════════════════════════════════════
        // ── Nested coroutines (baseline) ─────────────────────
        // ══════════════════════════════════════════════════════

        IEnumerator OuterCoroutine(int depth)
        {
            string depthLabel = "Depth_" + depth;
            yield return null;

            if (depth > 0)
            {
                // Each nested StartCoroutine allocates again
                yield return StartCoroutine(OuterCoroutine(depth - 1));
            }

            // Some allocation at this level
            var data = new List<float> { Time.time, Time.deltaTime };
        }

        // ══════════════════════════════════════════════════════
        // ── Optimized yield loops ────────────────────────────
        // ══════════════════════════════════════════════════════

        IEnumerator OptimizedWaitLoop()
        {
            while (true)
            {
                // Cached WaitForSeconds — no allocation per yield
                yield return m_CachedWait05;
                m_SharedSB.Clear();
                m_SharedSB.Append("Waited at frame ").Append(Time.frameCount);
            }
        }

        IEnumerator OptimizedEndOfFrameLoop()
        {
            while (true)
            {
                // Cached WaitForEndOfFrame — no allocation per yield
                yield return m_CachedEOF;
            }
        }

        IEnumerator OptimizedBoxedYieldLoop()
        {
            int counter = 0;
            while (true)
            {
                counter++;
                yield return null; // null is fine, no alloc
                yield return null; // yield null instead of boxing counter

                if (counter > 10000) counter = 0;
            }
        }

        // ══════════════════════════════════════════════════════
        // ── Optimized nested — single flat coroutine ─────────
        // ══════════════════════════════════════════════════════

        IEnumerator OptimizedFlatCoroutine(int depth)
        {
            // Single coroutine iterates over depth levels instead of recursively
            // starting new coroutines. Same depth of work, one coroutine start.
            for (int d = depth; d >= 0; d--)
            {
                m_SharedSB.Clear();
                m_SharedSB.Append("Depth_").Append(d);
                yield return null;
            }
        }
    }
}
