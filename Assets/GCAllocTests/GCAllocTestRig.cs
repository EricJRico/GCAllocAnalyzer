using UnityEngine;

namespace GCAllocTest
{
    /// <summary>
    /// Drop this on any GameObject (or an empty one) to add all test allocator
    /// components automatically. Or just use the menu item to create a test rig.
    ///
    /// Usage:
    ///   1. Menu: Tools > GC Alloc Test > Create Test Rig
    ///   2. Enter Play Mode
    ///   3. Let it run for 100+ frames
    ///   4. Open the Profiler (Ctrl+7 / Cmd+7)
    ///   5. Make sure "Call Stacks" → "GC.Alloc" is enabled in the Profiler toolbar
    ///   6. Open GC Alloc Analyzer (Window > Analysis > GC Alloc Analyzer)
    ///   7. Pull Data → set frame range → Analyze
    ///
    /// Each component can be toggled on/off in the Inspector.
    /// Individual allocation patterns within each component can also be toggled.
    /// </summary>
    public class GCAllocTestRig : MonoBehaviour
    {
#if UNITY_EDITOR
        [UnityEditor.MenuItem("Tools/GC Alloc Test/Create Test Rig")]
        static void CreateTestRig()
        {
            var existing = FindFirstObjectByType<GCAllocTestRig>();
            if (existing != null)
            {
                UnityEditor.Selection.activeGameObject = existing.gameObject;
                Debug.Log("GC Alloc Test Rig already exists in scene.");
                return;
            }

            var go = new GameObject("── GC Alloc Test Rig ──");

            // Add all test components
            go.AddComponent<GCAllocTestRig>();
            go.AddComponent<CommonAllocPatterns>();
            go.AddComponent<CoroutineAllocGenerator>();
            go.AddComponent<GCAllocTest.DataStructures.VariedSizeAllocator>();

            // These are in different namespaces / no namespace
            go.AddComponent<InitAndPeriodicAllocs>();
            go.AddComponent<GCAllocTest.Systems.EventDriven.EventAllocGenerator>();

            UnityEditor.Selection.activeGameObject = go;
            Debug.Log("Created GC Alloc Test Rig with all test components.");
        }

        [UnityEditor.MenuItem("Tools/GC Alloc Test/Remove Test Rig")]
        static void RemoveTestRig()
        {
            var existing = FindFirstObjectByType<GCAllocTestRig>();
            if (existing != null)
            {
                DestroyImmediate(existing.gameObject);
                Debug.Log("Removed GC Alloc Test Rig.");
            }
        }
#endif
    }
}
