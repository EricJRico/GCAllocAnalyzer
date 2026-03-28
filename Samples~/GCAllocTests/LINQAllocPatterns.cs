using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace GCAllocTest.LINQPatterns
{
    /// <summary>
    /// Exercises common LINQ operators that allocate enumerators and delegates every frame.
    /// Each pattern is togglable from the Inspector so you can isolate individual sources
    /// of GC pressure in the Profiler.
    /// </summary>
    public class LINQAllocPatterns : MonoBehaviour
    {
        // ═══════════════════════════════════════════════════════════════════
        //  Constants
        // ═══════════════════════════════════════════════════════════════════

        const int k_IntCount = 1000;
        const int k_StringCount = 100;
        const int k_DictCount = 50;

        // ═══════════════════════════════════════════════════════════════════
        //  Inspector Toggles
        // ═══════════════════════════════════════════════════════════════════

        [SerializeField] bool m_EnableFilter = true;
        [SerializeField] bool m_EnableProjection = true;
        [SerializeField] bool m_EnableSort = true;
        [SerializeField] bool m_EnableGroupBy = true;
        [SerializeField] bool m_EnableAggregate = true;
        [SerializeField] bool m_EnableAnyFirst = true;
        [SerializeField] bool m_EnableChained = true;
        [SerializeField] bool m_EnableDictionaryLinq = true;

        // ═══════════════════════════════════════════════════════════════════
        //  Private Fields
        // ═══════════════════════════════════════════════════════════════════

        List<int> m_IntList;
        List<string> m_StringList;
        Dictionary<string, int> m_DictData;
        int m_Threshold;
        int m_Frame;

        bool m_Optimized;
        List<int> m_FilterResult;
        List<string> m_ProjectionResult;
        List<int> m_SortBuffer;
        List<int> m_GroupResult;
        List<int> m_ChainedResult;
        List<string> m_DictLinqResult;
        StringBuilder m_AggregateSB;
        int[] m_GroupBuckets;
        Comparison<int> m_DescComparer;

        // ═══════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ═══════════════════════════════════════════════════════════════════

        void Start()
        {
            m_Threshold = 500;

            m_IntList = new List<int>(k_IntCount);
            for (int i = 0; i < k_IntCount; i++)
                m_IntList.Add(i);

            m_StringList = new List<string>(k_StringCount);
            for (int i = 0; i < k_StringCount; i++)
                m_StringList.Add("Item_" + i);

            m_DictData = new Dictionary<string, int>(k_DictCount);
            for (int i = 0; i < k_DictCount; i++)
                m_DictData.Add("Key_" + i, i * 7);

            m_Optimized = GCAllocTestRig.CurrentMode == AllocMode.Optimized;
            m_FilterResult = new List<int>(k_IntCount);
            m_ProjectionResult = new List<string>(64);
            m_SortBuffer = new List<int>(k_IntCount);
            m_GroupResult = new List<int>(10);
            m_ChainedResult = new List<int>(k_IntCount);
            m_DictLinqResult = new List<string>(k_DictCount);
            m_AggregateSB = new StringBuilder(512);
            m_GroupBuckets = new int[10];
            m_DescComparer = (a, b) => b.CompareTo(a);
        }

        void Update()
        {
            m_Frame++;

            if (m_EnableFilter)
                DoFilter();

            if (m_EnableProjection)
                DoProjection();

            if (m_EnableSort)
                DoSortTake();

            if (m_EnableGroupBy)
                DoGroupBy();

            if (m_EnableAggregate)
                DoAggregate();

            if (m_EnableAnyFirst)
                DoAnyFirst();

            if (m_EnableChained)
                DoChained();

            if (m_EnableDictionaryLinq)
                DoDictionaryLinq();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 1 — Filter
        //  Allocates: Where enumerator + delegate + ToList result
        // ═══════════════════════════════════════════════════════════════════

        void DoFilter()
        {
            if (m_Optimized)
            {
                m_FilterResult.Clear();
                for (int i = 0; i < m_IntList.Count; i++)
                    if (m_IntList[i] > m_Threshold)
                        m_FilterResult.Add(m_IntList[i]);
                return;
            }

            _ = m_IntList.Where(x => x > m_Threshold).ToList();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 2 — Projection
        //  Allocates: Where enumerator + Select enumerator + delegates +
        //             ToString per element + ToArray result
        // ═══════════════════════════════════════════════════════════════════

        void DoProjection()
        {
            if (m_Optimized)
            {
                m_ProjectionResult.Clear();
                for (int i = 0; i < m_IntList.Count; i++)
                    if (m_IntList[i] < 50)
                        m_ProjectionResult.Add(m_IntList[i].ToString());
                return;
            }

            _ = m_IntList.Where(x => x < 50).Select(x => x.ToString()).ToArray();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 3 — Sort + Take
        //  Allocates: OrderBy buffer + enumerator + delegate + Take
        //             enumerator + ToList result
        // ═══════════════════════════════════════════════════════════════════

        void DoSortTake()
        {
            if (m_Optimized)
            {
                m_SortBuffer.Clear();
                for (int i = 0; i < m_IntList.Count; i++)
                    m_SortBuffer.Add(m_IntList[i]);
                m_SortBuffer.Sort(m_DescComparer);
                if (m_SortBuffer.Count > 10)
                    m_SortBuffer.RemoveRange(10, m_SortBuffer.Count - 10);
                return;
            }

            _ = m_IntList.OrderBy(x => -x).Take(10).ToList();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 4 — GroupBy
        //  Allocates: GroupBy internal dictionaries + Grouping objects +
        //             Select enumerator + Sum enumerators + ToList result
        // ═══════════════════════════════════════════════════════════════════

        void DoGroupBy()
        {
            if (m_Optimized)
            {
                for (int i = 0; i < m_GroupBuckets.Length; i++)
                    m_GroupBuckets[i] = 0;
                for (int i = 0; i < m_IntList.Count; i++)
                    m_GroupBuckets[m_IntList[i] % 10] += m_IntList[i];
                m_GroupResult.Clear();
                for (int i = 0; i < m_GroupBuckets.Length; i++)
                    m_GroupResult.Add(m_GroupBuckets[i]);
                return;
            }

            _ = m_IntList.GroupBy(x => x % 10).Select(g => g.Sum()).ToList();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 5 — Aggregate (string concat fold)
        //  Allocates: Take enumerator + delegate + N intermediate strings
        //             (worst-case O(n^2) string allocation)
        // ═══════════════════════════════════════════════════════════════════

        void DoAggregate()
        {
            if (m_Optimized)
            {
                m_AggregateSB.Clear();
                int count = m_StringList.Count < 20 ? m_StringList.Count : 20;
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) m_AggregateSB.Append(", ");
                    m_AggregateSB.Append(m_StringList[i]);
                }
                return;
            }

            _ = m_StringList.Take(20).Aggregate("", (acc, x) => acc + ", " + x);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 6 — Any / First
        //  Allocates: enumerator + delegate per call (even though result
        //             is a single bool/int)
        // ═══════════════════════════════════════════════════════════════════

        void DoAnyFirst()
        {
            if (m_Optimized)
            {
                bool found = false;
                for (int i = 0; i < m_IntList.Count; i++)
                {
                    if (m_IntList[i] == 42) { found = true; break; }
                }
                _ = found;

                int first = default;
                for (int i = 0; i < m_IntList.Count; i++)
                {
                    if (m_IntList[i] > 900) { first = m_IntList[i]; break; }
                }
                _ = first;
                return;
            }

            _ = m_IntList.Any(x => x == 42);
            _ = m_IntList.FirstOrDefault(x => x > 900);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 7 — Chained operators
        //  Allocates: Where + Select + OrderByDescending + Take enumerators
        //             + all delegates + sort buffer + ToList result
        //             (cascading enumerator allocations)
        // ═══════════════════════════════════════════════════════════════════

        void DoChained()
        {
            if (m_Optimized)
            {
                m_ChainedResult.Clear();
                for (int i = 0; i < m_IntList.Count; i++)
                    if (m_IntList[i] > 100)
                        m_ChainedResult.Add(m_IntList[i] * 2);
                m_ChainedResult.Sort(m_DescComparer);
                if (m_ChainedResult.Count > 5)
                    m_ChainedResult.RemoveRange(5, m_ChainedResult.Count - 5);
                return;
            }

            _ = m_IntList
                .Where(x => x > 100)
                .Select(x => x * 2)
                .OrderByDescending(x => x)
                .Take(5)
                .ToList();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 8 — Dictionary LINQ
        //  Allocates: Values enumerator + Where enumerator + Select
        //             enumerator + delegates + ToString per element +
        //             ToList result
        // ═══════════════════════════════════════════════════════════════════

        void DoDictionaryLinq()
        {
            if (m_Optimized)
            {
                m_DictLinqResult.Clear();
                foreach (var kvp in m_DictData)
                    if (kvp.Value > 100)
                        m_DictLinqResult.Add(kvp.Value.ToString());
                return;
            }

            _ = m_DictData.Values.Where(v => v > 100).Select(v => v.ToString()).ToList();
        }
    }
}
