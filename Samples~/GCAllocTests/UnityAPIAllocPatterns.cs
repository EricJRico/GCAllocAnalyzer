using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GCAllocTest.UnityAPIs
{
    // ═══════════════════════════════════════════════════════════════════
    //  UnityAPIAllocPatterns
    //  Exercises Unity API calls that secretly allocate managed memory.
    //  Each category can be toggled independently from the Inspector.
    // ═══════════════════════════════════════════════════════════════════
    public class UnityAPIAllocPatterns : MonoBehaviour
    {
        // ═══════════════════════════════════════════════════════════════
        //  Nested Types
        // ═══════════════════════════════════════════════════════════════

        [System.Serializable]
        class SerializablePayload
        {
            public int id;
            public string name;
            public float[] values;
        }

        // ═══════════════════════════════════════════════════════════════
        //  Inspector Toggles
        // ═══════════════════════════════════════════════════════════════

        [SerializeField] bool m_EnableMesh = true;
        [SerializeField] bool m_EnableMaterial = true;
        [SerializeField] bool m_EnablePhysics = true;
        [SerializeField] bool m_EnableStringAPIs = true;
        [SerializeField] bool m_EnableHierarchy = true;
        [SerializeField] bool m_EnableSerialization = true;

        // ═══════════════════════════════════════════════════════════════
        //  Private Fields
        // ═══════════════════════════════════════════════════════════════

        int m_Frame;
        GameObject m_Child;
        MeshFilter m_ChildMeshFilter;
        Renderer m_ChildRenderer;
        Collider m_ChildCollider;
        Rigidbody m_Rigidbody;
        RaycastHit[] m_RaycastHitBuffer;

        // ── Optimized-mode fields ────────────────────────────────────
        bool m_Optimized;
        List<Vector3> m_VertexList;
        List<Vector3> m_NormalList;
        List<int> m_TriList;
        List<Vector2> m_UVList;
        Collider[] m_OverlapBuffer;
        string m_CachedName;
        string m_CachedTag;
        string m_CachedChildName;
        List<Transform> m_TransformListBuffer;
        SerializablePayload m_CachedPayload;
        StringBuilder m_SharedSB;

        // ═══════════════════════════════════════════════════════════════
        //  Setup & Teardown
        // ═══════════════════════════════════════════════════════════════

        void Awake()
        {
            // Create a primitive cube as a child — gives us MeshFilter,
            // MeshRenderer, and BoxCollider for free.
            m_Child = GameObject.CreatePrimitive(PrimitiveType.Cube);
            m_Child.name = "APITest_Child";
            m_Child.tag = "Untagged";
            m_Child.transform.SetParent(transform);

            // Cache child components
            m_ChildMeshFilter = m_Child.GetComponent<MeshFilter>();
            m_ChildRenderer = m_Child.GetComponent<Renderer>();
            m_ChildCollider = m_Child.GetComponent<Collider>();

            // Add a kinematic Rigidbody to self (for physics query relevance)
            m_Rigidbody = gameObject.AddComponent<Rigidbody>();
            m_Rigidbody.isKinematic = true;

            // Pre-allocate buffer for NonAlloc contrast
            m_RaycastHitBuffer = new RaycastHit[32];

            // Optimized-mode setup
            m_Optimized = GCAllocTestRig.CurrentMode == AllocMode.Optimized;
            m_VertexList = new List<Vector3>();
            m_NormalList = new List<Vector3>();
            m_TriList = new List<int>();
            m_UVList = new List<Vector2>();
            m_OverlapBuffer = new Collider[32];
            m_CachedName = gameObject.name;
            m_CachedTag = gameObject.tag;
            m_CachedChildName = m_Child.name;
            m_TransformListBuffer = new List<Transform>();
            m_CachedPayload = new SerializablePayload { values = new float[3] };
            m_SharedSB = new StringBuilder(128);
        }

        void OnDestroy()
        {
            if (m_Child != null)
                Destroy(m_Child);
        }

        // ═══════════════════════════════════════════════════════════════
        //  Update Loop
        // ═══════════════════════════════════════════════════════════════

        void Update()
        {
            m_Frame++;

            if (m_EnableMesh && m_Frame % 5 == 0)
                DoMeshAllocations();

            if (m_EnableMaterial && m_Frame % 30 == 0)
                DoMaterialAllocations();

            if (m_EnablePhysics)
                DoPhysicsAllocations();

            if (m_EnableStringAPIs)
                DoStringAPIAllocations();

            if (m_EnableHierarchy && m_Frame % 10 == 0)
                DoHierarchyAllocations();

            if (m_EnableSerialization && m_Frame % 20 == 0)
                DoSerializationAllocations();
        }

        // ═══════════════════════════════════════════════════════════════
        //  1. Mesh Property Getters — allocate new managed arrays
        // ═══════════════════════════════════════════════════════════════

        void DoMeshAllocations()
        {
            if (m_Optimized)
            {
                Mesh mesh = m_ChildMeshFilter.sharedMesh;
                mesh.GetVertices(m_VertexList);
                mesh.GetNormals(m_NormalList);
                mesh.GetTriangles(m_TriList, 0);
                mesh.GetUVs(0, m_UVList);
                return;
            }

            {
                Mesh mesh = m_ChildMeshFilter.sharedMesh;

                // Each getter allocates a fresh managed array every call
                _ = mesh.vertices;   // new Vector3[]
                _ = mesh.normals;    // new Vector3[]
                _ = mesh.triangles;  // new int[]
                _ = mesh.uv;         // new Vector2[]
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  2. Material Cloning — .material vs .sharedMaterial
        // ═══════════════════════════════════════════════════════════════

        void DoMaterialAllocations()
        {
            if (m_Optimized)
            {
                // Use sharedMaterial only — no clone, no allocation
                _ = m_ChildRenderer.sharedMaterial;
                _ = m_ChildRenderer.sharedMaterial;
                return;
            }

            // .material clones the shared material on first access per
            // renderer instance (allocates). Subsequent calls return the
            // existing clone.
            _ = m_ChildRenderer.material;

            // .sharedMaterial returns the original — no allocation.
            _ = m_ChildRenderer.sharedMaterial;

            // Explicit material clone to force a fresh allocation each time.
            var mat = new Material(m_ChildRenderer.sharedMaterial);
            Destroy(mat);
        }

        // ═══════════════════════════════════════════════════════════════
        //  3. Physics Queries — allocating vs NonAlloc variants
        // ═══════════════════════════════════════════════════════════════

        void DoPhysicsAllocations()
        {
            if (m_Optimized)
            {
                Vector3 origin = transform.position;
                Physics.RaycastNonAlloc(origin, Vector3.forward, m_RaycastHitBuffer, 100f);
                Physics.OverlapSphereNonAlloc(origin, 5f, m_OverlapBuffer);
                Physics.RaycastNonAlloc(origin, Vector3.forward, m_RaycastHitBuffer, 100f);
                return;
            }

            {
                Vector3 origin = transform.position;

                // These allocate fresh arrays every call
                _ = Physics.RaycastAll(origin, Vector3.forward, 100f);
                _ = Physics.OverlapSphere(origin, 5f);

                // Contrast: NonAlloc writes into a pre-allocated buffer — no alloc
                Physics.RaycastNonAlloc(origin, Vector3.forward, m_RaycastHitBuffer, 100f);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  4. String-Returning APIs — hidden string allocations
        // ═══════════════════════════════════════════════════════════════

        void DoStringAPIAllocations()
        {
            if (m_Optimized)
            {
                _ = m_CachedTag;
                _ = m_CachedName;
                _ = m_CachedChildName;
                _ = m_CachedChildName;
                _ = gameObject.CompareTag("Untagged");
                return;
            }

            // Each of these allocates a new managed string
            _ = gameObject.tag;
            _ = gameObject.name;
            _ = m_Child.name;

            // Deeper hierarchy traversal — still allocates a string
            _ = transform.GetChild(0).gameObject.name;

            // Contrast: CompareTag does NOT allocate
            _ = gameObject.CompareTag("Untagged");
        }

        // ═══════════════════════════════════════════════════════════════
        //  5. Transform / Hierarchy Iteration — enumerator & array allocs
        // ═══════════════════════════════════════════════════════════════

        void DoHierarchyAllocations()
        {
            if (m_Optimized)
            {
                // Index-based iteration — no enumerator allocation
                for (int i = 0; i < transform.childCount; i++)
                    _ = transform.GetChild(i);

                // List overload — fills existing list, no array allocation
                m_TransformListBuffer.Clear();
                GetComponentsInChildren(m_TransformListBuffer);

                // FindObjectsByType has no non-alloc variant — skip in optimized mode
                return;
            }

            // foreach over Transform allocates an enumerator
            foreach (Transform child in transform)
            {
                _ = child;
            }

            // GetComponentsInChildren allocates a new array
            _ = GetComponentsInChildren<Transform>();

            // FindObjectsByType allocates a new array (Unity 6 API)
            _ = FindObjectsByType<Transform>(FindObjectsSortMode.None);
        }

        // ═══════════════════════════════════════════════════════════════
        //  6. Serialization — JsonUtility round-trip allocations
        // ═══════════════════════════════════════════════════════════════

        void DoSerializationAllocations()
        {
            if (m_Optimized)
            {
                m_CachedPayload.id = m_Frame;
                m_SharedSB.Clear();
                m_SharedSB.Append("test_").Append(m_Frame);
                m_CachedPayload.name = m_SharedSB.ToString(); // one string alloc unavoidable
                m_CachedPayload.values[0] = 1f;
                m_CachedPayload.values[1] = 2f;
                m_CachedPayload.values[2] = 3f;

                var payloadString = JsonUtility.ToJson(m_CachedPayload); // unavoidable string alloc
                JsonUtility.FromJsonOverwrite(payloadString, m_CachedPayload); // reuse existing object
                return;
            }

            // Object + string + array allocation in the payload itself
            var payload = new SerializablePayload
            {
                id = m_Frame,
                name = "test_" + m_Frame,
                values = new float[] { 1f, 2f, 3f }
            };

            // ToJson allocates a string
            string json = JsonUtility.ToJson(payload);

            // FromJson allocates a new deserialized object
            _ = JsonUtility.FromJson<SerializablePayload>(json);
        }
    }
}
