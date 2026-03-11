# Burst-compiled jobs from Unity Editor Windows

**Burst-compiled jobs work in the Unity Editor outside Play Mode and can accelerate editor tools by 60–150×.** The `[BurstCompile]` attribute triggers JIT compilation for any job scheduled in the editor process — not just during gameplay — making it a powerful but underused technique for speeding up complex editor-side tasks like mesh processing, terrain generation, or batch data transforms. The key challenges are managing NativeContainer lifetimes across domain reloads and understanding the async JIT compilation model. This guide covers the full pattern: setup, scheduling, memory safety, pitfalls, and a complete working example.

## Burst's JIT compiler runs in Edit Mode, not just Play Mode

Despite the official Burst 1.8 documentation stating that "Burst compiles your code just-in-time (JIT) while in Play mode in the Editor," this phrasing contrasts Editor (JIT) versus Player builds (AOT) — it does not restrict Burst to Play Mode. Multiple pieces of evidence confirm Burst JIT fires in Edit Mode:

**Eager compilation** begins at editor startup and after every domain reload, compiling all discovered `[BurstCompile]`-tagged methods before any Play Mode entry. The Burst Inspector (`Jobs > Burst Inspector`) displays compiled jobs from all assemblies, including editor-only ones, and works entirely outside Play Mode. Direct Call support (Burst 1.5+) operates via IL post-processing at compile time, with no Play Mode dependency.

When you schedule a `[BurstCompile]`-tagged job from an EditorWindow, the Burst JIT compiles it just as it would during gameplay. However, **the first invocation may execute under Mono** because Burst compiles asynchronously in the background by default. For editor tools, always use `[BurstCompile(CompileSynchronously = true)]` to force immediate Burst compilation on first schedule — this blocks briefly but ensures every execution is Burst-optimized.

No special folder structure is required. Your editor assembly (in an `Editor` folder or with an editor-only `.asmdef`) simply needs references to `Unity.Burst`, `Unity.Collections`, `Unity.Jobs`, and `Unity.Mathematics`. Jobs defined inside editor-only assemblies are fully Burst-compilable and visible in the Burst Inspector.

## Scheduling jobs from OnGUI and handling completion

The Job System's `Schedule()` method must be called from the main thread. Since `OnGUI` runs on the main thread, scheduling from a button click is straightforward. The critical decision is whether to block the editor or complete asynchronously.

**Synchronous completion** is the simplest pattern — call `handle.Complete()` immediately after scheduling. This blocks the Unity Editor until the job finishes, which is perfectly acceptable for operations under ~100ms. For longer operations, it freezes the entire editor UI, but `EditorUtility.DisplayCancelableProgressBar` can provide feedback if you split work into batches.

**Asynchronous completion** uses `EditorWindow.Update()`, which fires roughly 100 times per second for visible windows. Schedule the job in response to a button click, store the `JobHandle`, and poll `IsCompleted` in `Update()`. This keeps the editor responsive during long computations. One critical rule: **even when `IsCompleted` returns true, you must still call `Complete()`** to clean up the safety system's internal state. Skipping this causes memory leaks.

A key `OnGUI` pitfall: this method fires multiple times per frame for different event types (Layout, Repaint, MouseDown). **Never schedule a job on every `OnGUI` call.** Guard scheduling with button clicks or state flags to prevent launching dozens of redundant jobs per frame. Additionally, call `Repaint()` when using async patterns to force the window to redraw and reflect updated state.

For long-running work, split the computation into batches. Schedule one batch, complete it in `Update()`, report progress via `EditorUtility.DisplayCancelableProgressBar` (blocking) or the `Progress` class (Unity 2020.1+, non-blocking background tasks), then schedule the next batch. This provides cancellation support and progress feedback.

## NativeContainer allocation, disposal, and the editor lifecycle

NativeContainers require explicit allocation and disposal. In editor code, the choice of allocator is crucial:

- **`Allocator.TempJob`** is appropriate only when you schedule, complete, and dispose within a single method call (synchronous pattern). It must be disposed within 4 editor update ticks or Unity logs persistent warnings.
- **`Allocator.Persistent`** is the right choice for NativeContainers that live across multiple editor frames — for instance, data held as EditorWindow fields for async job completion or reuse across multiple operations.
- **`Allocator.Temp`** cannot be passed to jobs and is unsuitable for this use case.

The EditorWindow lifecycle provides natural allocation/disposal boundaries. Allocate persistent NativeContainers in `OnEnable()` and dispose them in `OnDisable()`. This matters because **EditorWindows survive domain reloads**: Unity calls `OnDisable` before a reload and `OnEnable` after. If you don't dispose in `OnDisable`, the managed reference to native memory is lost during reload, causing an unrecoverable memory leak.

Always check `IsCreated` before calling `Dispose()` to prevent double-dispose exceptions. For the deferred async pattern, if a job is still running when the window closes, call `_jobHandle.Complete()` before disposing the containers it references — disposing a NativeArray while a job is writing to it will crash the editor.

## Domain reloads are the biggest editor-specific threat

Domain reloads — triggered by entering/exiting Play Mode or recompiling any script — are the most dangerous pitfall for editor jobs. During a domain reload, **all managed state is destroyed and reconstructed**, meaning your EditorWindow fields storing `JobHandle` and `NativeArray` references are wiped. Scheduled jobs are force-completed by Unity before the domain unloads, but any `Allocator.Persistent` containers not explicitly disposed will leak.

The defensive pattern requires two cleanup hooks:

```csharp
void OnEnable()
{
    AssemblyReloadEvents.beforeAssemblyReload += CleanupNativeResources;
}

void OnDisable()
{
    AssemblyReloadEvents.beforeAssemblyReload -= CleanupNativeResources;
    CleanupNativeResources();
}

void CleanupNativeResources()
{
    if (_jobRunning) { _jobHandle.Complete(); _jobRunning = false; }
    if (_data.IsCreated) _data.Dispose();
}
```

`AssemblyReloadEvents.beforeAssemblyReload` fires before a domain reload begins, giving you a window to force-complete jobs and dispose containers. Without this, Unity's `DisposeSentinel` will eventually detect the leak and log a warning, but the native memory is already lost.

Other editor-specific hazards include **async code pausing when Unity loses focus** (the `EditorApplication.update` pump stops), **`ShowModalUtility()` windows blocking `Update()` callbacks** (a known Unity bug), and **Burst safety checks causing 7–8× slowdown** in some Unity 2022 builds compared to 2021. Keep safety checks enabled during development for correctness, and only disable them during performance profiling via `Jobs > Burst > Safety Checks`.

## Complete working EditorWindow with Burst job

The following example demonstrates both synchronous and asynchronous patterns in a single EditorWindow. It processes a large array using a Burst-compiled parallel job and handles all editor lifecycle concerns:

```csharp
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

public class BurstEditorToolWindow : EditorWindow
{
    // --- Job Definitions ---
    
    [BurstCompile(CompileSynchronously = true)]
    struct HeavyComputeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> input;
        [WriteOnly] public NativeArray<float> output;
        public float multiplier;

        public void Execute(int index)
        {
            // Expensive math — Burst compiles this to SIMD instructions
            float val = input[index];
            for (int i = 0; i < 100; i++)
                val = math.sqrt(val * val + multiplier) * math.sin(val + i);
            output[index] = val;
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    struct ReductionJob : IJob
    {
        [ReadOnly] public NativeArray<float> data;
        [WriteOnly] public NativeArray<float> result;

        public void Execute()
        {
            float sum = 0f;
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int i = 0; i < data.Length; i++)
            {
                sum += data[i];
                min = math.min(min, data[i]);
                max = math.max(max, data[i]);
            }
            result[0] = sum / data.Length; // mean
            result[1] = min;
            result[2] = max;
        }
    }

    // --- Window State ---
    
    const int DataSize = 1_000_000;
    const int BatchSize = 256;

    // Async job state
    NativeArray<float> _input;
    NativeArray<float> _output;
    NativeArray<float> _reductionResult;
    JobHandle _jobHandle;
    bool _jobRunning;
    bool _dataAllocated;

    // Results display
    string _syncResultText = "Not run yet.";
    string _asyncResultText = "Not run yet.";
    double _lastElapsedMs;

    [MenuItem("Tools/Burst Editor Job Demo")]
    static void ShowWindow()
    {
        GetWindow<BurstEditorToolWindow>("Burst Job Demo");
    }

    // --- Lifecycle: allocate and dispose NativeContainers safely ---

    void OnEnable()
    {
        AssemblyReloadEvents.beforeAssemblyReload += CleanupNativeResources;
        AllocatePersistentData();
    }

    void OnDisable()
    {
        AssemblyReloadEvents.beforeAssemblyReload -= CleanupNativeResources;
        CleanupNativeResources();
    }

    void AllocatePersistentData()
    {
        if (_dataAllocated) return;
        _input = new NativeArray<float>(DataSize, Allocator.Persistent);
        _output = new NativeArray<float>(DataSize, Allocator.Persistent);
        _reductionResult = new NativeArray<float>(3, Allocator.Persistent);

        // Fill input with sample data
        for (int i = 0; i < DataSize; i++)
            _input[i] = (float)(i + 1);

        _dataAllocated = true;
    }

    void CleanupNativeResources()
    {
        if (_jobRunning)
        {
            _jobHandle.Complete();
            _jobRunning = false;
        }
        if (_input.IsCreated) _input.Dispose();
        if (_output.IsCreated) _output.Dispose();
        if (_reductionResult.IsCreated) _reductionResult.Dispose();
        _dataAllocated = false;
    }

    // --- GUI ---

    void OnGUI()
    {
        GUILayout.Label("Burst-Compiled Editor Job Demo", EditorStyles.boldLabel);
        GUILayout.Space(5);
        EditorGUILayout.HelpBox(
            $"Processing {DataSize:N0} elements with 100 math iterations each.",
            MessageType.Info);
        GUILayout.Space(10);

        // --- Synchronous pattern ---
        GUILayout.Label("Synchronous (blocking)", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledGroupScope(_jobRunning))
        {
            if (GUILayout.Button("Run Burst Job (Sync)"))
                RunJobSynchronous();
        }
        EditorGUILayout.LabelField("Result", _syncResultText);
        GUILayout.Space(10);

        // --- Asynchronous pattern ---
        GUILayout.Label("Asynchronous (non-blocking)", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledGroupScope(_jobRunning))
        {
            if (GUILayout.Button("Run Burst Job (Async)"))
                RunJobAsync();
        }
        if (_jobRunning)
        {
            EditorGUILayout.LabelField("Status", "Job running...");
            Repaint(); // Keep repainting so Update() polls
        }
        EditorGUILayout.LabelField("Result", _asyncResultText);
        GUILayout.Space(10);

        EditorGUILayout.LabelField("Last duration",
            $"{_lastElapsedMs:F2} ms");
    }

    // --- Synchronous: schedule, block, read, done ---

    void RunJobSynchronous()
    {
        if (!_dataAllocated) AllocatePersistentData();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Chain: parallel compute → reduction
        var computeJob = new HeavyComputeJob
        {
            input = _input, output = _output, multiplier = 1.5f
        };
        JobHandle computeHandle = computeJob.Schedule(DataSize, BatchSize);

        var reduceJob = new ReductionJob
        {
            data = _output, result = _reductionResult
        };
        JobHandle reduceHandle = reduceJob.Schedule(computeHandle);

        reduceHandle.Complete(); // Blocks here

        sw.Stop();
        _lastElapsedMs = sw.Elapsed.TotalMilliseconds;
        _syncResultText = $"Mean={_reductionResult[0]:F4}, " +
                          $"Min={_reductionResult[1]:F4}, " +
                          $"Max={_reductionResult[2]:F4}";
    }

    // --- Asynchronous: schedule now, complete in Update() ---

    void RunJobAsync()
    {
        if (!_dataAllocated) AllocatePersistentData();

        var computeJob = new HeavyComputeJob
        {
            input = _input, output = _output, multiplier = 2.0f
        };
        JobHandle computeHandle = computeJob.Schedule(DataSize, BatchSize);

        var reduceJob = new ReductionJob
        {
            data = _output, result = _reductionResult
        };
        _jobHandle = reduceJob.Schedule(computeHandle);
        _jobRunning = true;
        _lastElapsedMs = 0;
    }

    void Update()
    {
        if (_jobRunning && _jobHandle.IsCompleted)
        {
            _jobHandle.Complete(); // Must still call Complete()
            _jobRunning = false;
            _asyncResultText = $"Mean={_reductionResult[0]:F4}, " +
                               $"Min={_reductionResult[1]:F4}, " +
                               $"Max={_reductionResult[2]:F4}";
            Repaint();
        }
    }
}
```

Drop this file into an `Editor` folder. Open the window via `Tools > Burst Editor Job Demo`. The synchronous button blocks the editor briefly while the async button keeps the UI responsive. Both use Burst-compiled parallel jobs processing one million elements with 100 math iterations each.

## Performance considerations and optimization checklist

**Use `Unity.Mathematics` types everywhere.** Burst generates optimal SIMD instructions for `float3`, `float4`, `int4`, and `quaternion`. Using `Vector3` or `Mathf` falls back to scalar operations, sacrificing most of the vectorization benefit. The `math.*` functions (`math.sqrt`, `math.sin`, `math.min`) are Burst-intrinsic and compile to single hardware instructions.

**Batch size in `IJobParallelFor` matters.** The batch size parameter (the second argument to `Schedule`) controls how many iterations each worker thread processes at once. Too small (e.g., 1) creates excessive scheduling overhead; too large reduces parallelism. **Start with 32–256** and tune based on per-iteration cost. Cheap iterations need larger batches; expensive iterations can use smaller ones.

**Mark read-only fields with `[ReadOnly]`.** This isn't just documentation — it allows the safety system to permit concurrent reads from multiple jobs and enables Burst to generate better code by assuming no aliasing. For advanced cases, `[NoAlias]` on pointer parameters tells Burst that two pointers never reference the same memory, unlocking further optimizations.

**`CompileSynchronously = true` has a one-time cost.** The first schedule blocks for Burst compilation (typically 50–500ms depending on job complexity). Subsequent invocations use the cached compiled code instantly. For editor tools, this one-time cost is negligible compared to the ongoing speedup.

**Avoid managed allocations inside `Execute()`.** No `string`, no `List<T>`, no `new` on reference types. Burst compiles a subset of C# called HPC# — violating this triggers compilation errors. Exceptions are allowed in the editor (for debugging) but will crash standalone builds, so use them sparingly.

Real-world editor tool benchmarks demonstrate dramatic improvements: grid dilation operations drop from **50–70ms to under 1ms** (60× speedup), procedural noise generation on 128³ voxel spaces goes from **900ms to 6ms** (150× speedup), and batch distance computations across thousands of points become interactive rather than blocking. These gains make previously impractical editor workflows — like real-time preview of procedural generation or interactive spatial queries — entirely feasible.

## Conclusion

Burst-compiled jobs in editor tools follow the same API as runtime jobs but demand careful attention to two editor-specific realities: **NativeContainer lifecycle management across domain reloads** and **the async JIT compilation model**. The defensive pattern — allocate in `OnEnable`, dispose in both `OnDisable` and `AssemblyReloadEvents.beforeAssemblyReload`, always use `CompileSynchronously = true` — eliminates the most common failure modes. For synchronous one-shot operations, the `Schedule().Complete()` pattern inside a button handler is all you need. For longer computations, the `Update()` polling pattern with `IsCompleted` keeps the editor responsive. The performance payoff is substantial: **60–150× speedups** turn second-long blocking operations into imperceptible editor actions, making Burst an essential tool for anyone building non-trivial Unity editor workflows.