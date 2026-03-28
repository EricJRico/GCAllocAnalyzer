using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GCAllocTest.Systems.EventDriven
{
    /// <summary>
    /// Tests event/delegate allocations, deep call stacks, and nested namespaces.
    /// The analyzer should show the full namespace path and handle deep stacks.
    /// </summary>
    public class EventAllocGenerator : MonoBehaviour
    {
        [Header("Events")]
        [Tooltip("Subscribe/unsubscribe with lambda each frame")]
        public bool lambdaSubscription = true;

        [Tooltip("Invoke event that allocates in handlers")]
        public bool eventHandlerAllocs = true;

        [Header("Deep Call Stacks")]
        [Tooltip("Allocate at the bottom of a 6-deep call chain")]
        public bool deepCallStack = true;

        [Tooltip("Recursive allocation (controlled depth)")]
        public bool recursiveAlloc = true;
        public int recursionDepth = 5;

        // ═══════════════════════════════════════════════════════
        //  Optimized-mode fields
        // ═══════════════════════════════════════════════════════

        bool m_Optimized;
        StringBuilder m_SharedSB;
        Action<string> m_CachedLambdaHandler;

        internal static StringBuilder s_SharedSB;
        internal static float[] s_ContactBuffer;

        // Events
        public event Action<string> OnStatusChanged;
        public event Action<int, float> OnDamageDealt;

        // ═══════════════════════════════════════════════════════
        //  Lifecycle
        // ═══════════════════════════════════════════════════════

        void Awake()
        {
            m_Optimized = GCAllocTestRig.CurrentMode == AllocMode.Optimized;
            m_SharedSB = new StringBuilder(256);
            m_CachedLambdaHandler = HandleLambdaMessage;
            s_SharedSB = m_SharedSB;
            s_ContactBuffer = new float[3];
        }

        void OnEnable()
        {
            // These are fine — instance method delegates allocated once
            OnStatusChanged += HandleStatusChanged;
            OnDamageDealt += HandleDamageDealt;
        }

        void OnDisable()
        {
            OnStatusChanged -= HandleStatusChanged;
            OnDamageDealt -= HandleDamageDealt;
        }

        void Update()
        {
            if (lambdaSubscription) DoLambdaSubscription();
            if (eventHandlerAllocs) DoEventHandlerAllocs();
            if (deepCallStack) DoDeepCallStack();
            if (recursiveAlloc)
            {
                if (m_Optimized) m_SharedSB.Clear();
                DoRecursiveAlloc(recursionDepth, "root");
            }
        }

        // ═══════════════════════════════════════════════════════
        //  Optimized-mode handler for cached lambda
        // ═══════════════════════════════════════════════════════

        void HandleLambdaMessage(string msg)
        {
            m_SharedSB.Clear();
            m_SharedSB.Append(msg).Append(Time.frameCount);
        }

        // ═══════════════════════════════════════════════════════
        //  Lambda subscription churn
        // ═══════════════════════════════════════════════════════

        void DoLambdaSubscription()
        {
            if (m_Optimized)
            {
                OnStatusChanged += m_CachedLambdaHandler;
                OnStatusChanged?.Invoke("tick");
                OnStatusChanged -= m_CachedLambdaHandler;
                return;
            }

            int frame = Time.frameCount;

            // This creates a new closure + delegate each frame
            Action<string> handler = msg => Debug.Log(msg + frame);
            OnStatusChanged += handler;
            OnStatusChanged?.Invoke("tick");
            OnStatusChanged -= handler;
        }

        // ═══════════════════════════════════════════════════════
        //  Event handler allocations
        // ═══════════════════════════════════════════════════════

        void DoEventHandlerAllocs()
        {
            // Invoke events — handlers will allocate
            OnStatusChanged?.Invoke("health_changed");
            OnDamageDealt?.Invoke(25, Time.time);
        }

        void HandleStatusChanged(string status)
        {
            if (m_Optimized)
            {
                m_SharedSB.Clear();
                m_SharedSB.Append("[Status] ").Append(status).Append(" at ").Append(Time.time);
                return;
            }
            // Allocates via string concat
            string log = "[Status] " + status + " at " + Time.time;
        }

        void HandleDamageDealt(int amount, float time)
        {
            if (m_Optimized)
            {
                m_SharedSB.Clear();
                m_SharedSB.Append("Damage: ").Append(amount).Append(" at t=").Append(time);
                return;
            }
            // Allocates via boxing + string format
            string log = string.Format("Damage: {0} at t={1:F2}", amount, time);
        }

        // ═══════════════════════════════════════════════════════
        //  Deep call stacks
        // ═══════════════════════════════════════════════════════

        void DoDeepCallStack()
        {
            SystemManager.ProcessFrame(Time.frameCount);
        }

        // ═══════════════════════════════════════════════════════
        //  Recursive allocation
        // ═══════════════════════════════════════════════════════

        void DoRecursiveAlloc(int depth, string prefix)
        {
            if (m_Optimized)
            {
                DoRecursiveAllocOpt(depth);
                return;
            }
            if (depth <= 0) return;

            // Allocates a new string at each recursion level
            string next = prefix + "." + depth;
            DoRecursiveAlloc(depth - 1, next);
        }

        void DoRecursiveAllocOpt(int depth)
        {
            if (depth <= 0) return;
            m_SharedSB.Append('.').Append(depth);
            DoRecursiveAllocOpt(depth - 1);
        }
    }

    /// <summary>
    /// Static helper class — tests that static methods appear correctly in call stacks.
    /// Creates a 6-deep call chain: ProcessFrame → UpdateSystems → RunPhysics →
    /// ResolveContacts → AllocateContactData → CreateContactString
    /// </summary>
    public static class SystemManager
    {
        public static void ProcessFrame(int frame)
        {
            UpdateSystems(frame);
        }

        static void UpdateSystems(int frame)
        {
            RunPhysics(frame);
        }

        static void RunPhysics(int frame)
        {
            ResolveContacts(frame, 3);
        }

        static void ResolveContacts(int frame, int contactCount)
        {
            for (int i = 0; i < contactCount; i++)
                AllocateContactData(frame, i);
        }

        static void AllocateContactData(int frame, int index)
        {
            if (GCAllocTestRig.CurrentMode == AllocMode.Optimized)
            {
                var sb = EventAllocGenerator.s_SharedSB;
                sb.Clear();
                sb.Append("Contact_").Append(frame).Append("_").Append(index);
                var buf = EventAllocGenerator.s_ContactBuffer;
                buf[0] = index;
                buf[1] = frame;
                buf[2] = UnityEngine.Time.deltaTime;
                return;
            }
            // The actual allocation — deep in the stack
            string contactInfo = CreateContactString(frame, index);
            var contactData = new float[] { index, frame, UnityEngine.Time.deltaTime };
        }

        static string CreateContactString(int frame, int index)
        {
            return "Contact_" + frame + "_" + index;
        }
    }
}
