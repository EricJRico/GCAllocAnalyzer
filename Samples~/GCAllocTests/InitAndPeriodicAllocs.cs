using System.Collections.Generic;
using System.Text;
using GCAllocTest;
using UnityEngine;

/// <summary>
/// NO NAMESPACE — tests that the analyzer handles global-namespace scripts.
/// Allocates heavily on initialization, then periodically creates spikes.
/// </summary>
public class InitAndPeriodicAllocs : MonoBehaviour
{
    [Header("Initialization")]
    [Tooltip("Number of strings to allocate in Awake")]
    public int awakeStringCount = 200;

    [Tooltip("Number of objects to allocate in Start")]
    public int startObjectCount = 100;

    [Header("Periodic Spikes")]
    [Tooltip("Allocate a large buffer every N frames")]
    public int spikeIntervalFrames = 60;

    [Tooltip("Size of spike buffer in bytes")]
    public int spikeBufferSize = 65536; // 64 KB

    [Header("Low-Frequency")]
    [Tooltip("Small allocation every N frames")]
    public int lowFreqIntervalFrames = 10;

    int m_Frame;
    List<string> m_CachedStrings;
    List<object> m_CachedObjects;
    bool m_Optimized;
    StringBuilder m_SharedSB;
    byte[] m_SpikeBuffer;
    Dictionary<string, string> m_MetadataDict;
    float[] m_SnapshotBuffer;

    void Awake()
    {
        m_Optimized = GCAllocTestRig.CurrentMode == AllocMode.Optimized;
        m_SharedSB = new StringBuilder(128);

        if (m_Optimized)
        {
            // Use StringBuilder to avoid intermediate concat strings
            m_CachedStrings = new List<string>(awakeStringCount);
            for (int i = 0; i < awakeStringCount; i++)
            {
                m_SharedSB.Clear();
                m_SharedSB.Append("InitString_").Append(i);
                m_CachedStrings.Add(m_SharedSB.ToString());
            }
        }
        else
        {
            // Heavy initialization — many small string allocations
            m_CachedStrings = new List<string>(awakeStringCount);
            for (int i = 0; i < awakeStringCount; i++)
            {
                m_CachedStrings.Add("InitString_" + i.ToString());
            }
        }

        m_SharedSB.Clear();
        m_SharedSB.Append("InitAndPeriodicAllocs: Awake allocated ").Append(awakeStringCount).Append(" strings");
        Debug.Log(m_SharedSB.ToString());

        // Pre-allocate reusable buffers
        m_SpikeBuffer = new byte[spikeBufferSize];
        m_MetadataDict = new Dictionary<string, string>(3);
        m_SnapshotBuffer = new float[3];
    }

    void Start()
    {
        if (m_Optimized)
        {
            // Single large buffer instead of N small arrays
            m_CachedObjects = new List<object>(1);
            int totalSize = 0;
            for (int i = 0; i < startObjectCount; i++)
                totalSize += 32 + i;
            m_CachedObjects.Add(new byte[totalSize]);
        }
        else
        {
            // More initialization — boxed objects and arrays
            m_CachedObjects = new List<object>(startObjectCount);
            for (int i = 0; i < startObjectCount; i++)
            {
                m_CachedObjects.Add(new byte[32 + i]);
            }
        }

        m_SharedSB.Clear();
        m_SharedSB.Append("InitAndPeriodicAllocs: Start allocated ").Append(startObjectCount).Append(" objects");
        Debug.Log(m_SharedSB.ToString());
    }

    void Update()
    {
        m_Frame++;

        // Large spike every N frames
        if (m_Frame % spikeIntervalFrames == 0)
        {
            CreateSpike();
        }

        // Small allocation every N frames
        if (m_Frame % lowFreqIntervalFrames == 0)
        {
            SmallPeriodicAlloc();
        }
    }

    void CreateSpike()
    {
        // ═══════════════════════════════════════════════════════════════════
        // Optimized: reuse pre-allocated buffer, interned string literals
        // ═══════════════════════════════════════════════════════════════════
        if (m_Optimized)
        {
            m_SpikeBuffer[0] = 0xFF;
            m_MetadataDict.Clear();
            m_MetadataDict["timestamp"] = "cached";
            m_MetadataDict["frame"] = "cached";
            m_MetadataDict["size"] = "cached";
            return;
        }

        // One large allocation — simulates loading a texture buffer, mesh data, etc.
        byte[] buffer = new byte[spikeBufferSize];
        buffer[0] = 0xFF;

        // Plus some associated metadata allocations
        var metadata = new Dictionary<string, string>
        {
            { "timestamp", Time.time.ToString("F3") },
            { "frame", m_Frame.ToString() },
            { "size", spikeBufferSize.ToString() }
        };
    }

    void SmallPeriodicAlloc()
    {
        // ═══════════════════════════════════════════════════════════════════
        // Optimized: SB for string, reuse float buffer
        // ═══════════════════════════════════════════════════════════════════
        if (m_Optimized)
        {
            m_SharedSB.Clear();
            m_SharedSB.Append("Frame ").Append(m_Frame).Append(" alive");
            m_SnapshotBuffer[0] = Time.time;
            m_SnapshotBuffer[1] = Time.deltaTime;
            m_SnapshotBuffer[2] = Time.fixedDeltaTime;
            return;
        }

        // Small intermittent allocation — common in real code
        string status = "Frame " + m_Frame + " alive";
        var snapshot = new float[] { Time.time, Time.deltaTime, Time.fixedDeltaTime };
    }
}
