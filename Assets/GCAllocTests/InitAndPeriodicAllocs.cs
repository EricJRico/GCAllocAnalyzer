using System.Collections.Generic;
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

    void Awake()
    {
        // Heavy initialization — many small string allocations
        m_CachedStrings = new List<string>(awakeStringCount);
        for (int i = 0; i < awakeStringCount; i++)
        {
            m_CachedStrings.Add("InitString_" + i.ToString());
        }

        Debug.Log("InitAndPeriodicAllocs: Awake allocated " + awakeStringCount + " strings");
    }

    void Start()
    {
        // More initialization — boxed objects and arrays
        m_CachedObjects = new List<object>(startObjectCount);
        for (int i = 0; i < startObjectCount; i++)
        {
            m_CachedObjects.Add(new byte[32 + i]);
        }

        Debug.Log("InitAndPeriodicAllocs: Start allocated " + startObjectCount + " objects");
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
        // Small intermittent allocation — common in real code
        string status = "Frame " + m_Frame + " alive";
        var snapshot = new float[] { Time.time, Time.deltaTime, Time.fixedDeltaTime };
    }
}
