using UnityEngine;

namespace GCAllocTest
{
    public enum AllocMode { Baseline, Optimized }

    [CreateAssetMenu(fileName = "TestProfile", menuName = "GC Alloc Test/Test Profile")]
    public class TestProfile : ScriptableObject
    {
        public AllocMode mode = AllocMode.Baseline;
    }
}
