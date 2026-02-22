using System;
using System.Collections.Generic;
using UnityEngine;

namespace GCAllocTest
{
    /// <summary>
    /// Generates common GC allocation patterns every frame.
    /// Attach to a GameObject to produce a variety of per-frame allocations.
    /// Toggle individual patterns on/off in the Inspector.
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

        // Internal state for closures
        int m_FrameCount;
        readonly List<int> m_ReusableList = new List<int>(64);

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

        // ── String allocations ──────────────────────────────

        void DoStringConcat()
        {
            // Each + allocates a new string
            string result = "Frame " + m_FrameCount + " position " + transform.position;
            ConsumeString(result);
        }

        void DoStringFormat()
        {
            string result = string.Format("Object {0} at ({1:F2}, {2:F2}, {3:F2})",
                "Player", transform.position.x, transform.position.y, transform.position.z);
            ConsumeString(result);
        }

        void DoValueTypeToString()
        {
            // .ToString() on value types allocates
            string a = m_FrameCount.ToString();
            string b = transform.position.ToString();
            string c = Time.deltaTime.ToString("F4");
            ConsumeString(a);
            ConsumeString(b);
            ConsumeString(c);
        }

        // ── Collection allocations ──────────────────────────

        void DoNewList()
        {
            // Allocates a new List + its internal array
            var list = new List<Vector3>(16);
            for (int i = 0; i < 10; i++)
                list.Add(Vector3.one * i);
        }

        void DoNewDict()
        {
            var dict = new Dictionary<string, int>(8)
            {
                { "health", 100 },
                { "mana", 50 },
                { "stamina", 75 }
            };
        }

        void DoListToArray()
        {
            m_ReusableList.Clear();
            for (int i = 0; i < 20; i++)
                m_ReusableList.Add(i);

            // ToArray allocates a new array
            int[] arr = m_ReusableList.ToArray();
        }

        // ── Boxing ──────────────────────────────────────────

        void DoBoxingObject()
        {
            // Passing value type as object causes boxing
            LogValue(m_FrameCount);
            LogValue(3.14f);
            LogValue(true);
        }

        static void LogValue(object value)
        {
            // The allocation happens at the call site, not here
        }

        void DoBoxingInterface()
        {
            IComparable boxed = m_FrameCount; // boxes the int
            boxed.CompareTo(42);
        }

        // ── Closures & Delegates ────────────────────────────

        void DoClosureCapture()
        {
            int localValue = m_FrameCount;

            // This lambda captures localValue, generating a closure class allocation
            System.Func<int, int> adder = x => x + localValue;
            adder(10);
        }

        void DoNewDelegate()
        {
            // New Action allocation each frame
            Action callback = OnCallback;
            callback();
        }

        void OnCallback() { }

        // ── Unity API allocations ───────────────────────────

        void DoGameObjectName()
        {
            // .name returns a new string from native each time
            string n = gameObject.name;
            ConsumeString(n);
        }

        void DoGameObjectTag()
        {
            string t = gameObject.tag;
            ConsumeString(t);
        }

        void DoGetComponents()
        {
            // Allocates a new array every call
            Component[] all = gameObject.GetComponents<Component>();
        }

        // Prevent compiler from optimizing away unused strings
        static void ConsumeString(string s)
        {
            if (s == null) throw new Exception("never");
        }
    }
}
