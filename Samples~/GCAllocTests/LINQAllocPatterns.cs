using System.Collections.Generic;
using System.Linq;
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
            _ = m_IntList.Where(x => x > m_Threshold).ToList();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 2 — Projection
        //  Allocates: Where enumerator + Select enumerator + delegates +
        //             ToString per element + ToArray result
        // ═══════════════════════════════════════════════════════════════════

        void DoProjection()
        {
            _ = m_IntList.Where(x => x < 50).Select(x => x.ToString()).ToArray();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 3 — Sort + Take
        //  Allocates: OrderBy buffer + enumerator + delegate + Take
        //             enumerator + ToList result
        // ═══════════════════════════════════════════════════════════════════

        void DoSortTake()
        {
            _ = m_IntList.OrderBy(x => -x).Take(10).ToList();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 4 — GroupBy
        //  Allocates: GroupBy internal dictionaries + Grouping objects +
        //             Select enumerator + Sum enumerators + ToList result
        // ═══════════════════════════════════════════════════════════════════

        void DoGroupBy()
        {
            _ = m_IntList.GroupBy(x => x % 10).Select(g => g.Sum()).ToList();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 5 — Aggregate (string concat fold)
        //  Allocates: Take enumerator + delegate + N intermediate strings
        //             (worst-case O(n^2) string allocation)
        // ═══════════════════════════════════════════════════════════════════

        void DoAggregate()
        {
            _ = m_StringList.Take(20).Aggregate("", (acc, x) => acc + ", " + x);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 6 — Any / First
        //  Allocates: enumerator + delegate per call (even though result
        //             is a single bool/int)
        // ═══════════════════════════════════════════════════════════════════

        void DoAnyFirst()
        {
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
            _ = m_DictData.Values.Where(v => v > 100).Select(v => v.ToString()).ToList();
        }
    }
}
