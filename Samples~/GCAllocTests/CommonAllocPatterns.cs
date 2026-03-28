using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GCAllocTest
{
    /// <summary>
    /// Generates common GC allocation patterns every frame.
    /// Attach to a GameObject to produce a variety of per-frame allocations.
    /// Toggle individual patterns on/off in the Inspector.
    /// When the test rig is set to Optimized mode, each pattern performs the
    /// same logical work but avoids GC allocations.
    /// </summary>
    public class CommonAllocPatterns : MonoBehaviour
    {
        [Header("String Allocations")]
        [Tooltip("String concatenation in a loop")]
        public bool stringConcat = true;

        [Tooltip("String.Format calls")]
        public bool stringFormat = true;

        [Tooltip("ToString on value types (boxing)")]
        public bool valueTypeToString = true;

        [Header("Collection Allocations")]
        [Tooltip("New List<> every frame")]
        public bool newListEveryFrame = true;

        [Tooltip("New Dictionary<> every frame")]
        public bool newDictEveryFrame = true;

        [Tooltip("List.ToArray() copy")]
        public bool listToArray = true;

        [Header("Boxing")]
        [Tooltip("Box int via object param")]
        public bool boxingViaObject = true;

        [Tooltip("Box struct via interface")]
        public bool boxingViaInterface = true;

        [Header("Closures & Delegates")]
        [Tooltip("Lambda capturing local variable")]
        public bool closureCapture = true;

        [Tooltip("New Action delegate each frame")]
        public bool newDelegateEveryFrame = true;

        [Header("Unity API")]
        [Tooltip("GameObject.name access (returns new string)")]
        public bool gameObjectName = true;

        [Tooltip("GameObject.tag access")]
        public bool gameObjectTag = true;

        [Tooltip("GetComponents<> (allocates array)")]
        public bool getComponents = true;

        // ═══════════════════════════════════════════════════════
        // Internal state
        // ═══════════════════════════════════════════════════════

        int m_FrameCount;
        readonly List<int> m_ReusableList = new List<int>(64);

        // ═══════════════════════════════════════════════════════
        // Optimized-mode infrastructure
        // ═══════════════════════════════════════════════════════

        bool m_Optimized;
        StringBuilder m_SharedSB;
        List<Vector3> m_ReusableVec3List;
        Dictionary<string, int> m_ReusableDict;
        string m_CachedName;
        string m_CachedTag;
        List<Component> m_ComponentListBuffer;
        Action m_CachedCallback;
        Func<int, int> m_CachedAdder;

        void Awake()
        {
            m_Optimized = GCAllocTestRig.CurrentMode == AllocMode.Optimized;
            m_SharedSB = new StringBuilder(256);
            m_ReusableVec3List = new List<Vector3>(16);
            m_ReusableDict = new Dictionary<string, int>(8);
            m_CachedName = gameObject.name;
            m_CachedTag = gameObject.tag;
            m_ComponentListBuffer = new List<Component>();
            m_CachedCallback = OnCallback;
            m_CachedAdder = x => x + m_FrameCount;
        }

        void Update()
        {
            m_FrameCount++;

            if (stringConcat) DoStringConcat();
            if (stringFormat) DoStringFormat();
            if (valueTypeToString) DoValueTypeToString();
            if (newListEveryFrame) DoNewList();
            if (newDictEveryFrame) DoNewDict();
            if (listToArray) DoListToArray();
            if (boxingViaObject) DoBoxingObject();
            if (boxingViaInterface) DoBoxingInterface();
            if (closureCapture) DoClosureCapture();
            if (newDelegateEveryFrame) DoNewDelegate();
            if (gameObjectName) DoGameObjectName();
            if (gameObjectTag) DoGameObjectTag();
            if (getComponents) DoGetComponents();
        }

        // ═══════════════════════════════════════════════════════
        // String allocations
        // ═══════════════════════════════════════════════════════

        void DoStringConcat()
        {
            if (m_Optimized)
            {
                m_SharedSB.Clear();
                m_SharedSB.Append("Frame ").Append(m_FrameCount)
                    .Append(" position ").Append(transform.position);
                ConsumeString(m_SharedSB);
                return;
            }

            // Each + allocates a new string
            string result = "Frame " + m_FrameCount + " position " + transform.position;
            ConsumeString(result);
        }

        void DoStringFormat()
        {
            if (m_Optimized)
            {
                m_SharedSB.Clear();
                var pos = transform.position;
                m_SharedSB.Append("Object Player at (")
                    .Append(pos.x).Append(", ")
                    .Append(pos.y).Append(", ")
                    .Append(pos.z).Append(")");
                ConsumeString(m_SharedSB);
                return;
            }

            string result = string.Format("Object {0} at ({1:F2}, {2:F2}, {3:F2})",
                "Player", transform.position.x, transform.position.y, transform.position.z);
            ConsumeString(result);
        }

        void DoValueTypeToString()
        {
            if (m_Optimized)
            {
                m_SharedSB.Clear();
                m_SharedSB.Append(m_FrameCount);
                m_SharedSB.Append(transform.position.x).Append(", ")
                    .Append(transform.position.y).Append(", ")
                    .Append(transform.position.z);
                m_SharedSB.Append(Time.deltaTime);
                ConsumeString(m_SharedSB);
                return;
            }

            // .ToString() on value types allocates
            string a = m_FrameCount.ToString();
            string b = transform.position.ToString();
            string c = Time.deltaTime.ToString("F4");
            ConsumeString(a);
            ConsumeString(b);
            ConsumeString(c);
        }

        // ═══════════════════════════════════════════════════════
        // Collection allocations
        // ═══════════════════════════════════════════════════════

        void DoNewList()
        {
            if (m_Optimized)
            {
                m_ReusableVec3List.Clear();
                for (int i = 0; i < 10; i++)
                    m_ReusableVec3List.Add(Vector3.one * i);
                return;
            }

            // Allocates a new List + its internal array
            var list = new List<Vector3>(16);
            for (int i = 0; i < 10; i++)
                list.Add(Vector3.one * i);
        }

        void DoNewDict()
        {
            if (m_Optimized)
            {
                m_ReusableDict.Clear();
                m_ReusableDict["health"] = 100;
                m_ReusableDict["mana"] = 50;
                m_ReusableDict["stamina"] = 75;
                return;
            }

            var dict = new Dictionary<string, int>(8)
            {
                { "health", 100 },
                { "mana", 50 },
                { "stamina", 75 }
            };
        }

        void DoListToArray()
        {
            if (m_Optimized)
            {
                m_ReusableList.Clear();
                for (int i = 0; i < 20; i++)
                    m_ReusableList.Add(i);

                // Iterate by index instead of ToArray
                for (int i = 0; i < m_ReusableList.Count; i++)
                {
                    _ = m_ReusableList[i];
                }
                return;
            }

            m_ReusableList.Clear();
            for (int i = 0; i < 20; i++)
                m_ReusableList.Add(i);

            // ToArray allocates a new array
            int[] arr = m_ReusableList.ToArray();
        }

        // ═══════════════════════════════════════════════════════
        // Boxing
        // ═══════════════════════════════════════════════════════

        void DoBoxingObject()
        {
            if (m_Optimized)
            {
                // Generic overload avoids boxing
                LogValue(m_FrameCount);
                LogValue(3.14f);
                LogValue(true);
                return;
            }

            // Passing value type as object causes boxing
            LogValueBoxed(m_FrameCount);
            LogValueBoxed(3.14f);
            LogValueBoxed(true);
        }

        static void LogValueBoxed(object value)
        {
            // The allocation happens at the call site, not here
        }

        static void LogValue<T>(T value)
        {
            // Generic: no boxing
        }

        void DoBoxingInterface()
        {
            if (m_Optimized)
            {
                // Direct call on int, no interface assignment
                _ = m_FrameCount.CompareTo(42);
                return;
            }

            IComparable boxed = m_FrameCount; // boxes the int
            boxed.CompareTo(42);
        }

        // ═══════════════════════════════════════════════════════
        // Closures & Delegates
        // ═══════════════════════════════════════════════════════

        void DoClosureCapture()
        {
            if (m_Optimized)
            {
                // Cached Func reads m_FrameCount field — no closure class
                m_CachedAdder(10);
                return;
            }

            int localValue = m_FrameCount;

            // This lambda captures localValue, generating a closure class allocation
            System.Func<int, int> adder = x => x + localValue;
            adder(10);
        }

        void DoNewDelegate()
        {
            if (m_Optimized)
            {
                // Cached Action — no allocation
                m_CachedCallback();
                return;
            }

            // New Action allocation each frame
            Action callback = OnCallback;
            callback();
        }

        void OnCallback() { }

        // ═══════════════════════════════════════════════════════
        // Unity API allocations
        // ═══════════════════════════════════════════════════════

        void DoGameObjectName()
        {
            if (m_Optimized)
            {
                ConsumeString(m_CachedName);
                return;
            }

            // .name returns a new string from native each time
            string n = gameObject.name;
            ConsumeString(n);
        }

        void DoGameObjectTag()
        {
            if (m_Optimized)
            {
                ConsumeString(m_CachedTag);
                return;
            }

            string t = gameObject.tag;
            ConsumeString(t);
        }

        void DoGetComponents()
        {
            if (m_Optimized)
            {
                // Non-allocating overload fills existing list
                m_ComponentListBuffer.Clear();
                gameObject.GetComponents(m_ComponentListBuffer);
                return;
            }

            // Allocates a new array every call
            Component[] all = gameObject.GetComponents<Component>();
        }

        // ═══════════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════════

        // Prevent compiler from optimizing away unused strings
        static void ConsumeString(string s)
        {
            if (s == null) throw new Exception("never");
        }

        static void ConsumeString(StringBuilder sb)
        {
            if (sb == null) throw new Exception("never");
        }
    }
}
