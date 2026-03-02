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
            Mesh mesh = m_ChildMeshFilter.sharedMesh;

            // Each getter allocates a fresh managed array every call
            _ = mesh.vertices;   // new Vector3[]
            _ = mesh.normals;    // new Vector3[]
            _ = mesh.triangles;  // new int[]
            _ = mesh.uv;         // new Vector2[]
        }

        // ═══════════════════════════════════════════════════════════════
        //  2. Material Cloning — .material vs .sharedMaterial
        // ═══════════════════════════════════════════════════════════════

        void DoMaterialAllocations()
        {
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
            Vector3 origin = transform.position;

            // These allocate fresh arrays every call
            _ = Physics.RaycastAll(origin, Vector3.forward, 100f);
            _ = Physics.OverlapSphere(origin, 5f);

            // Contrast: NonAlloc writes into a pre-allocated buffer — no alloc
            Physics.RaycastNonAlloc(origin, Vector3.forward, m_RaycastHitBuffer, 100f);
        }

        // ═══════════════════════════════════════════════════════════════
        //  4. String-Returning APIs — hidden string allocations
        // ═══════════════════════════════════════════════════════════════

        void DoStringAPIAllocations()
        {
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
