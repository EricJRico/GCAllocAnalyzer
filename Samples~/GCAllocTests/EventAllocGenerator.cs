using System;
using System.Collections.Generic;
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

        // Events
        public event Action<string> OnStatusChanged;
        public event Action<int, float> OnDamageDealt;

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
            if (recursiveAlloc) DoRecursiveAlloc(recursionDepth, "root");
        }

        // ── Lambda subscription churn ───────────────────────

        void DoLambdaSubscription()
        {
            int frame = Time.frameCount;

            // This creates a new closure + delegate each frame
            Action<string> handler = msg => Debug.Log(msg + frame);
            OnStatusChanged += handler;
            OnStatusChanged?.Invoke("tick");
            OnStatusChanged -= handler;
        }

        // ── Event handler allocations ───────────────────────

        void DoEventHandlerAllocs()
        {
            // Invoke events — handlers will allocate
            OnStatusChanged?.Invoke("health_changed");
            OnDamageDealt?.Invoke(25, Time.time);
        }

        void HandleStatusChanged(string status)
        {
            // Allocates via string concat
            string log = "[Status] " + status + " at " + Time.time;
        }

        void HandleDamageDealt(int amount, float time)
        {
            // Allocates via boxing + string format
            string log = string.Format("Damage: {0} at t={1:F2}", amount, time);
        }

        // ── Deep call stacks ────────────────────────────────

        void DoDeepCallStack()
        {
            SystemManager.ProcessFrame(Time.frameCount);
        }

        // ── Recursive allocation ────────────────────────────

        void DoRecursiveAlloc(int depth, string prefix)
        {
            if (depth <= 0) return;

            // Allocates a new string at each recursion level
            string next = prefix + "." + depth;
            DoRecursiveAlloc(depth - 1, next);
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
