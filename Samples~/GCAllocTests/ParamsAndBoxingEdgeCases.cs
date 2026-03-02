using System.Collections.Generic;
using UnityEngine;

namespace GCAllocTest.EdgeCases
{
    // ═══════════════════════════════════════════════════════════════════
    //  Supporting Types
    // ═══════════════════════════════════════════════════════════════════

    enum TestState { Idle, Running, Paused, Stopped, Error }

    struct ComparableStruct : System.IComparable<ComparableStruct>
    {
        public int Value;
        public int CompareTo(ComparableStruct other) => Value.CompareTo(other.Value);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Static Helper Classes
    // ═══════════════════════════════════════════════════════════════════

    static class ParamsHelper
    {
        /// <summary>
        /// Consumes a params array. The access to args.Length prevents the
        /// compiler from optimizing away the allocation.
        /// </summary>
        public static void ConsumeParams(params object[] args)
        {
            _ = args.Length;
        }

        /// <summary>
        /// Calls ConsumeParams with various argument counts to demonstrate
        /// how each call site allocates a fresh object[] plus boxes value types.
        /// </summary>
        public static void CallWithParams(int frame)
        {
            // object[1] + boxes int
            ConsumeParams(frame);

            // object[3] + boxes int + boxes float
            ConsumeParams(frame, 2.5f, "hello");

            // object[5] + boxes 5 ints
            ConsumeParams(frame, frame + 1, frame + 2, frame + 3, frame + 4);
        }
    }

    static class EnumHelper
    {
        /// <summary>
        /// Performs several enum operations that each trigger boxing allocations.
        /// </summary>
        public static string ProcessState(TestState state)
        {
            // Boxes enum to call object.ToString()
            string name = state.ToString();

            // Boxes both enum values for the Enum.HasFlag call
            _ = state.HasFlag(TestState.Paused);

            // Boxes enum for string interpolation
            _ = $"State is: {state}";

            return name;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MonoBehaviour — ParamsAndBoxingEdgeCases
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Exercises subtle GC allocation patterns: params arrays, enum boxing,
    /// interface dispatch boxing, nullable boxing, string edge cases, and
    /// collection resizing. Each pattern is togglable from the Inspector.
    /// </summary>
    public class ParamsAndBoxingEdgeCases : MonoBehaviour
    {
        // ═══════════════════════════════════════════════════════════════
        //  Inspector-Togglable Patterns
        // ═══════════════════════════════════════════════════════════════

        [Header("Toggle Individual Allocation Patterns")]
        [SerializeField] bool m_EnableParams = true;
        [SerializeField] bool m_EnableEnumBoxing = true;
        [SerializeField] bool m_EnableInterfaceBoxing = true;
        [SerializeField] bool m_EnableNullable = true;
        [SerializeField] bool m_EnableStringEdgeCases = true;
        [SerializeField] bool m_EnableCollectionResizing = true;

        // ═══════════════════════════════════════════════════════════════
        //  Private Fields
        // ═══════════════════════════════════════════════════════════════

        int m_Frame;
        Dictionary<TestState, int> m_EnumDict;

        // ═══════════════════════════════════════════════════════════════
        //  Lifecycle
        // ═══════════════════════════════════════════════════════════════

        void Start()
        {
            // No custom comparer — the default comparer boxes the enum key
            // on every GetHashCode / Equals call.
            m_EnumDict = new Dictionary<TestState, int>();
        }

        void Update()
        {
            m_Frame++;

            if (m_EnableParams)
                DoParamsAllocations();

            if (m_EnableEnumBoxing)
                DoEnumBoxing();

            if (m_EnableInterfaceBoxing && m_Frame % 5 == 0)
                DoInterfaceBoxing();

            if (m_EnableNullable)
                DoNullableBoxing();

            if (m_EnableStringEdgeCases)
                DoStringEdgeCases();

            if (m_EnableCollectionResizing && m_Frame % 10 == 0)
                DoCollectionResizing();
        }

        // ═══════════════════════════════════════════════════════════════
        //  1. Params Arrays
        //  Every call to a params method allocates a new object[] on the
        //  heap, and every value-type argument is boxed into it.
        // ═══════════════════════════════════════════════════════════════

        void DoParamsAllocations()
        {
            // Calls through a static helper to add stack depth
            ParamsHelper.CallWithParams(m_Frame);

            // string.Format with 4 int args — each int is boxed even though
            // the 4-arg overload exists, because the parameters are object.
            _ = string.Format("{0} {1} {2} {3}", m_Frame, m_Frame + 1, m_Frame + 2, m_Frame + 3);
        }

        // ═══════════════════════════════════════════════════════════════
        //  2. Enum Boxing
        //  Enum methods like ToString, HasFlag, and GetHashCode box the
        //  enum value. Dictionary<TEnum, V> without a custom comparer
        //  boxes on every lookup.
        // ═══════════════════════════════════════════════════════════════

        void DoEnumBoxing()
        {
            // Call through static helper for stack depth
            _ = EnumHelper.ProcessState(TestState.Running);

            // Dictionary keying without comparer — boxes key for
            // GetHashCode and Equals on both store and lookup.
            m_EnumDict[TestState.Running] = m_Frame;
            _ = m_EnumDict[TestState.Running];

            // String interpolation boxes the enum for its ToString call
            _ = $"Current state: {TestState.Running}";
        }

        // ═══════════════════════════════════════════════════════════════
        //  3. Interface Dispatch Boxing
        //  Assigning a struct to an interface variable boxes it. Generic
        //  methods constrained to an interface can also box depending on
        //  the runtime and JIT.
        // ═══════════════════════════════════════════════════════════════

        void DoInterfaceBoxing()
        {
            // Explicit assignment to interface — boxes the struct
            ComparableStruct s = new ComparableStruct { Value = 42 };
            System.IComparable<ComparableStruct> boxed = s;
            _ = boxed.CompareTo(new ComparableStruct { Value = 10 });

            // Generic method with interface constraint — the constraint
            // may force boxing depending on the runtime implementation.
            _ = DoCompare(
                new ComparableStruct { Value = 1 },
                new ComparableStruct { Value = 2 });
        }

        /// <summary>
        /// Generic comparison through an interface constraint. When called
        /// with a struct type, the interface dispatch can trigger boxing.
        /// </summary>
        T DoCompare<T>(T a, T b) where T : System.IComparable<T>
        {
            return a.CompareTo(b) < 0 ? a : b;
        }

        // ═══════════════════════════════════════════════════════════════
        //  4. Nullable Boxing
        //  Assigning a Nullable<T> to an object reference boxes the
        //  underlying value. Passing it to a method that accepts object
        //  does the same at the call site.
        // ═══════════════════════════════════════════════════════════════

        void DoNullableBoxing()
        {
            // Nullable int — boxes the int when assigned to object
            int? nullableInt = 42;
            object boxedInt = nullableInt;
            _ = boxedInt;

            // Nullable Vector3 — boxes the entire 12-byte struct
            Vector3? nullableVec = Vector3.one;
            object boxedVec = nullableVec;
            _ = boxedVec;

            // Passing nullable through an object parameter forces boxing
            // at the call site.
            AcceptObject(nullableInt);
            AcceptObject(nullableVec);
        }

        /// <summary>
        /// Accepts any object. When called with a value type or nullable,
        /// the caller must box the argument.
        /// </summary>
        static void AcceptObject(object obj)
        {
            _ = obj;
        }

        // ═══════════════════════════════════════════════════════════════
        //  5. String Edge Cases
        //  Concatenation chains, += in loops, and formatting with value
        //  types all produce intermediate string allocations and boxing.
        // ═══════════════════════════════════════════════════════════════

        void DoStringEdgeCases()
        {
            // 5-part concatenation — the compiler chains Concat calls,
            // producing intermediate strings for each step.
            _ = "a" + "b" + "c" + m_Frame.ToString() + "e";

            // Repeated += in a loop — each iteration allocates a new string.
            string result = "";
            for (int i = 0; i < 10; i++)
                result += i.ToString();
            _ = result;

            // string.Format with a float format specifier — boxes the float
            _ = string.Format("{0:F2}", 3.14159f);

            // Interpolated string with format specifier — boxes the int
            _ = $"{m_Frame:N0}";
        }

        // ═══════════════════════════════════════════════════════════════
        //  6. Collection Resizing
        //  Starting with a small capacity and adding many elements triggers
        //  capacity doublings. Each resize allocates a new backing array,
        //  leaving the old one for GC.
        // ═══════════════════════════════════════════════════════════════

        void DoCollectionResizing()
        {
            // List<int> starts at capacity 4, doubles at 4→8→16→32→64→128
            // to hold 100 elements. Each doubling allocates a new int[].
            var list = new List<int>(4);
            for (int i = 0; i < 100; i++)
                list.Add(i);

            // Dictionary starts at capacity 2, rehashes/resizes multiple
            // times to hold 20 entries. Each resize allocates new buckets
            // and entries arrays. The "Key_" + i also allocates a string.
            var dict = new Dictionary<string, int>(2);
            for (int i = 0; i < 20; i++)
                dict["Key_" + i] = i;
        }
    }
}
