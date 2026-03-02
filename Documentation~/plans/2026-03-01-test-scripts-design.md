# GC Alloc Test Scripts — Expanded Coverage Design

**Date:** 2026-03-01
**Branch:** TBD (off `develop`)
**Goal:** Add 5 new test scripts covering gaps in allocation pattern coverage: deep callstacks, threading, LINQ, Unity API allocations, and subtle boxing/params patterns.

---

## Existing Coverage

| Script | Patterns | Max Depth | Threading |
|--------|----------|-----------|-----------|
| CommonAllocPatterns | Strings, collections, boxing, delegates, closures | 1–2 | Main |
| CoroutineAllocGenerator | WaitForSeconds, enumerators, nested coroutines | 3–5 | Main |
| EventAllocGenerator | Closures, delegates, static call chains, recursion | 6 | Main |
| InitAndPeriodicAllocs | Large buffer spikes, periodic small allocs | 1–2 | Main |
| VariedSizeAllocator | Size spectrum 16B–256KB, nested classes, generics | 1–2 | Main |

**Gaps:** No threading, max depth 6, no LINQ, no Unity API getters, no params/enum boxing edge cases.

---

## New Scripts

### 1. DeepCallstackAllocator

**Namespace:** `GCAllocTest.DeepStacks`
**File:** `Assets/GCAllocTests/DeepCallstackAllocator.cs`

**Purpose:** Generate allocations at configurable call depths (10–30+) to test callstack resolution and grouping.

**Patterns:**

- **Linear chain** (every frame): `Level01()` → `Level02()` → ... → `Level_N()` → allocates. Spread across 3 static helper classes (`ServiceA` → `ServiceB` → `ServiceC`) to simulate layered architecture. Target: 15–20 levels deep.
- **Recursive** (every 5 frames): Single method calls itself N times, allocating at each level and at the leaf. Configurable depth (default 12). Different allocation type at each level so they group differently.
- **Mixed** (every 15 frames): Chain that passes through instance methods and static methods across classes, with a recursive segment in the middle. Simulates framework-like code (event system → handler → serializer → recursive tree walk → string alloc).

**Inspector fields:**
- `bool enableLinearChain = true`
- `bool enableRecursive = true`
- `bool enableMixed = true`
- `int chainDepth = 18`
- `int recursionDepth = 12`

---

### 2. ThreadedAllocator

**Namespace:** `GCAllocTest.Threading`
**File:** `Assets/GCAllocTests/ThreadedAllocator.cs`

**Purpose:** Generate GC allocations on non-main threads to test thread attribution in the analyzer.

**Patterns:**

- **Raw Thread** (continuous): Spawns a named thread (`"GCTest_Worker"`) on Start. Loop allocates strings, byte arrays, and `List<int>` each iteration with 16ms sleep. 2–3 call depth within thread body. Stopped on OnDestroy.
- **ThreadPool** (every 30 frames): `ThreadPool.QueueUserWorkItem` builds a `Dictionary<string, List<int>>` with several entries, does string concat. Tests unnamed/indexed pool threads.
- **Task.Run** (every 10 frames): Fires `Task.Run` that allocates StringBuilder, appends in a loop, calls `.ToString()`, creates small object graph. Tests Task state machine allocation.

**Key constraint:** No Unity API calls from threads — pure C# allocations only (strings, collections, arrays, custom objects). Each pattern uses distinct type names for identification.

**Inspector fields:**
- `bool enableRawThread = true`
- `bool enableThreadPool = true`
- `bool enableTaskRun = true`

---

### 3. LINQAllocPatterns

**Namespace:** `GCAllocTest.LINQPatterns`
**File:** `Assets/GCAllocTests/LINQAllocPatterns.cs`

**Purpose:** Exercise common LINQ operators that allocate enumerators and delegates, a major real-world GC source.

**Setup:** Maintains `List<int>` (1000 elements) and `List<string>` (100 elements) populated on Start.

**Patterns (each frame, togglable):**

- **Filter** — `.Where(x => x > threshold).ToList()`
- **Projection** — `.Select(x => x.ToString()).ToArray()`
- **Sort + take** — `.OrderBy(x => x).Take(10).ToList()`
- **GroupBy** — `.GroupBy(x => x % 10).Select(g => g.Sum()).ToList()`
- **Aggregate** — `.Aggregate("", (acc, x) => acc + x)` (string concat fold)
- **Any/First** — `.Any(x => x == target)`, `.FirstOrDefault(x => x > target)`
- **Chained** — `.Where().Select().OrderByDescending().Take(5).ToList()`
- **Dictionary LINQ** — `.Values.Where().Select()` on a `Dictionary<string, int>`

Each pattern is a separate method at 2 levels deep from Update with its own Inspector toggle.

**Inspector fields:**
- `bool enableFilter = true`
- `bool enableProjection = true`
- `bool enableSort = true`
- `bool enableGroupBy = true`
- `bool enableAggregate = true`
- `bool enableAnyFirst = true`
- `bool enableChained = true`
- `bool enableDictionaryLinq = true`

---

### 4. UnityAPIAllocPatterns

**Namespace:** `GCAllocTest.UnityAPIs`
**File:** `Assets/GCAllocTests/UnityAPIAllocPatterns.cs`

**Purpose:** Exercise Unity API calls that secretly allocate, attributed to user calling code.

**Self-contained setup:** Adds needed components on Awake (MeshFilter with built-in cube mesh, BoxCollider, Rigidbody) so no manual scene setup is required.

**Patterns by category:**

**Mesh property getters** (every 5 frames):
- `mesh.vertices`, `mesh.normals`, `mesh.triangles`, `mesh.uv` — each returns new array

**Material cloning** (every 30 frames):
- `renderer.material.color = Color.red` — clones material on first `.material` access
- Contrast: reads `.sharedMaterial` (no alloc)

**Physics queries** (every frame):
- `Physics.RaycastAll(origin, dir, 100f)` — allocates hit array
- `Physics.OverlapSphere(pos, 5f)` — allocates collider array
- Contrast: `Physics.RaycastNonAlloc` into pre-allocated buffer

**String-returning APIs** (every frame):
- `gameObject.tag` — allocates (vs `CompareTag()`)
- `gameObject.name` — new string each access
- `transform.GetChild(0).gameObject.name` — deeper stack

**Transform/hierarchy iteration** (every 10 frames):
- `foreach (Transform child in transform)` — allocates enumerator
- `GetComponentsInChildren<Transform>()` — allocates array
- `FindObjectsByType<Transform>(FindObjectsSortMode.None)` — allocates array

**Serialization** (every 20 frames):
- `JsonUtility.ToJson(smallObject)` — string allocation
- `JsonUtility.FromJson<T>(jsonString)` — object allocation

**Inspector fields:**
- `bool enableMesh = true`
- `bool enableMaterial = true`
- `bool enablePhysics = true`
- `bool enableStringAPIs = true`
- `bool enableHierarchy = true`
- `bool enableSerialization = true`

---

### 5. ParamsAndBoxingEdgeCases

**Namespace:** `GCAllocTest.EdgeCases`
**File:** `Assets/GCAllocTests/ParamsAndBoxingEdgeCases.cs`

**Purpose:** Catch subtle, hard-to-spot allocation patterns — params arrays, enum boxing, interface dispatch, nullable boxing, string edge cases, collection resizing.

**Patterns:**

**Params arrays** (every frame):
- `void LogValues(params object[] args)` called with 1, 3, and 5 args — allocates `object[]` + boxes value types
- `string.Format` with 4+ args — falls off 3-arg overload, allocates params array
- Contrast: same call with pre-allocated array

**Enum boxing** (every frame):
- `enum.ToString()` — boxes enum, allocates string
- `enum.HasFlag(other)` — boxes both operands
- `Dictionary<MyEnum, int>` without custom comparer — boxes on lookup
- `$"State is {myEnum}"` — boxes for interpolation

**Interface dispatch boxing** (every 5 frames):
- Struct implementing `IComparable<T>` assigned to interface variable — boxes
- `IEnumerable<int>` from struct enumerator — boxes
- Generic method with struct vs interface constraint

**Nullable boxing** (every frame):
- `int? val = 42; object boxed = val;` — boxes
- `Nullable<Vector3>` round-tripping through `object`

**String edge cases** (every frame):
- `string.Concat(a, b, c, d, e)` — 5-arg params overload
- Repeated `+=` in loop vs StringBuilder
- `$"{value:F2}"` with value types — boxes for formatting

**Collection resizing** (every 10 frames):
- `new List<int>(4)` → add 100 elements — triggers capacity doublings (4→8→16→32→64→128)
- `Dictionary` growing past load factor

Each category in its own method, some with 2–3 level helper chains.

**Inspector fields:**
- `bool enableParams = true`
- `bool enableEnumBoxing = true`
- `bool enableInterfaceBoxing = true`
- `bool enableNullable = true`
- `bool enableStringEdgeCases = true`
- `bool enableCollectionResizing = true`

---

## Test Rig Update

`GCAllocTestRig.CreateTestRig()` adds 5 new components alongside existing 5:

```csharp
go.AddComponent<GCAllocTest.DeepStacks.DeepCallstackAllocator>();
go.AddComponent<GCAllocTest.Threading.ThreadedAllocator>();
go.AddComponent<GCAllocTest.LINQPatterns.LINQAllocPatterns>();
go.AddComponent<GCAllocTest.UnityAPIs.UnityAPIAllocPatterns>();
go.AddComponent<GCAllocTest.EdgeCases.ParamsAndBoxingEdgeCases>();
```

---

## Coverage Summary

| Script | Gap Filled | Threading | Max Depth |
|--------|-----------|-----------|-----------|
| DeepCallstackAllocator | 15–30+ level stacks | Main | 30+ |
| ThreadedAllocator | Off-main-thread allocs | Worker/Pool/Task | 3–5 |
| LINQAllocPatterns | LINQ enumerator chains | Main | 3–4 |
| UnityAPIAllocPatterns | Hidden Unity API allocs | Main | 3–5 |
| ParamsAndBoxingEdgeCases | Subtle boxing/params | Main | 3–5 |

## Conventions

- All scripts follow existing test patterns: `MonoBehaviour`, `Update()` driven, Inspector toggleable
- `m_` prefix for private fields, `k_` for constants
- No assembly definition (compile into default assembly like existing tests)
- Each script is self-contained — no dependencies between test scripts
