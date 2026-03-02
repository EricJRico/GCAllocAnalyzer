using System.Collections;
using System.Collections.Generic;
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

        void Start()
        {
            // Cache one WaitForSeconds to show the zero-alloc pattern
            m_CachedWait = new WaitForSeconds(0.1f);

            if (yieldNewWait) StartCoroutine(AllocatingWaitLoop());
            if (yieldEndOfFrame) StartCoroutine(EndOfFrameLoop());
            if (yieldBoxedValue) StartCoroutine(BoxedYieldLoop());
        }

        void Update()
        {
            m_Frame++;

            if (startCoroutineEveryN && m_Frame % coroutineInterval == 0)
            {
                // Each StartCoroutine call allocates a Coroutine object + enumerator
                StartCoroutine(ShortLivedCoroutine());
            }

            if (nestedCoroutines && m_Frame % (coroutineInterval * 2) == 0)
            {
                StartCoroutine(OuterCoroutine(nestingDepth));
            }
        }

        // ── Yield allocations ───────────────────────────────

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

        // ── Short-lived coroutine ───────────────────────────

        IEnumerator ShortLivedCoroutine()
        {
            // Enumerator + Coroutine allocated on start
            string label = "ShortLived_" + m_Frame;
            yield return null;
            // Done — but the allocations already happened
        }

        // ── Nested coroutines ───────────────────────────────

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
    }
}
