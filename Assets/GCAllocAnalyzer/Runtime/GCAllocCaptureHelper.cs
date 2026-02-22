using UnityEngine;
using UnityEngine.Profiling;

namespace GCAllocBreakdown
{
    /// <summary>
    /// Attach to a GameObject in your scene to enable allocation call stack
    /// recording and (optionally) .raw file capture for off-device analysis.
    ///
    /// For device profiling:
    ///   1. Enable "Development Build" in Build Settings
    ///   2. Attach this component to a persistent GameObject
    ///   3. Set writeRawFile = true to capture to disk
    ///   4. After the session, pull the .raw file from Application.persistentDataPath:
    ///      Android: adb pull /sdcard/Android/data/{bundleId}/files/profiler_capture.raw
    ///      iOS:     Xcode → Devices → Download Container → AppData/Documents/
    ///   5. In the Editor: Window → Analysis → GC Alloc Analyzer → Load .raw File
    /// </summary>
    public class GCAllocCaptureHelper : MonoBehaviour
    {
        [Header("Call Stacks")]
        [Tooltip("Enable managed allocation call stacks. Required for per-line " +
                 "breakdown in the GC Alloc Breakdown module.")]
        public bool enableCallStacks = true;

        [Header("Raw File Capture (for device profiling)")]
        [Tooltip("Write profiler data to a .raw file on disk for offline analysis.")]
        public bool writeRawFile = false;

        [Tooltip("Maximum memory buffer for profiler data (bytes). " +
                 "Increase if you get truncated captures.")]
        public int maxProfilerMemory = 256 * 1024 * 1024; // 256 MB

        [Header("Runtime Controls")]
        [Tooltip("Key to toggle capture on/off at runtime.")]
        public KeyCode toggleKey = KeyCode.F9;

        bool m_IsCapturing;
        string m_FilePath;

        void Start()
        {
            if (enableCallStacks)
            {
                Profiler.enableAllocationCallstacks = true;
                Debug.Log("[GCAllocCapture] Allocation call stacks ENABLED.");
            }

            if (writeRawFile)
                StartCapture();
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                if (m_IsCapturing) StopCapture();
                else StartCapture();
            }
        }

        void OnApplicationQuit()
        {
            if (m_IsCapturing)
                StopCapture();
        }

        void OnDestroy()
        {
            if (m_IsCapturing)
                StopCapture();
        }

        void StartCapture()
        {
            m_FilePath = System.IO.Path.Combine(
                Application.persistentDataPath, "profiler_capture");

            Profiler.maxUsedMemory = maxProfilerMemory;
            Profiler.logFile = m_FilePath;
            Profiler.enableBinaryLog = true;
            Profiler.enabled = true;
            m_IsCapturing = true;

            Debug.Log($"[GCAllocCapture] Started capture → {m_FilePath}.raw\n" +
                      $"  Press {toggleKey} to stop. " +
                      $"Buffer: {maxProfilerMemory / (1024 * 1024)} MB");
        }

        void StopCapture()
        {
            Profiler.enabled = false;
            Profiler.logFile = ""; // Flush and close
            Profiler.enableBinaryLog = false;
            m_IsCapturing = false;

            Debug.Log($"[GCAllocCapture] Stopped capture. File saved to:\n" +
                      $"  {m_FilePath}.raw\n" +
                      $"  Pull from device and load in GC Alloc Analyzer window.");
        }
    }
}
