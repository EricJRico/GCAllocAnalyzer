using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace GCAllocTest.Threading
{
    /// <summary>
    /// Generates GC allocations on non-main threads so a profiler analyzer
    /// can test thread attribution. Three patterns: raw Thread, ThreadPool,
    /// and Task.Run, each togglable from the Inspector.
    /// </summary>
    public class ThreadedAllocator : MonoBehaviour
    {
        // ═══════════════════════════════════════════════════════════════════
        //  Constants
        // ═══════════════════════════════════════════════════════════════════

        const int k_WorkerSleepMs = 16;
        const int k_PoolFrameInterval = 30;
        const int k_TaskFrameInterval = 10;
        const int k_PoolDictionaryCapacity = 4;
        const int k_WorkerListCapacity = 16;
        const int k_WorkerListElements = 10;
        const int k_WorkerBufferSize = 256;
        const int k_TaskStringBuilderCapacity = 128;
        const int k_TaskAppendIterations = 20;
        const int k_TaskPayloadBufferSize = 64;
        const int k_WorkerStringCount = 3;
        const int k_ThreadJoinTimeoutMs = 1000;

        // ═══════════════════════════════════════════════════════════════════
        //  Nested Types
        // ═══════════════════════════════════════════════════════════════════

        class TaskPayload
        {
            public int Id;
            public byte[] Data;
            public string Tag;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Inspector Fields
        // ═══════════════════════════════════════════════════════════════════

        [SerializeField] bool m_EnableRawThread = true;
        [SerializeField] bool m_EnableThreadPool = true;
        [SerializeField] bool m_EnableTaskRun = true;

        // ═══════════════════════════════════════════════════════════════════
        //  Private Fields
        // ═══════════════════════════════════════════════════════════════════

        volatile bool m_Running;
        Thread m_WorkerThread;
        int m_Frame;

        // ═══════════════════════════════════════════════════════════════════
        //  MonoBehaviour Lifecycle
        // ═══════════════════════════════════════════════════════════════════

        void Start()
        {
            m_Frame = 0;

            if (m_EnableRawThread)
            {
                m_Running = true;
                m_WorkerThread = new Thread(WorkerThreadLoop)
                {
                    Name = "GCTest_Worker",
                    IsBackground = true
                };
                m_WorkerThread.Start();
            }
        }

        void Update()
        {
            m_Frame++;

            if (m_EnableThreadPool && m_Frame % k_PoolFrameInterval == 0)
                ThreadPool.QueueUserWorkItem(PoolWorkItem);

            if (m_EnableTaskRun && m_Frame % k_TaskFrameInterval == 0)
            {
                int frameNum = m_Frame;
                _ = Task.Run(() => TaskWorkBody(frameNum));
            }
        }

        void OnDestroy()
        {
            m_Running = false;

            if (m_WorkerThread != null)
            {
                m_WorkerThread.Join(k_ThreadJoinTimeoutMs);
                m_WorkerThread = null;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 1: Raw Thread
        // ═══════════════════════════════════════════════════════════════════

        void WorkerThreadLoop()
        {
            int iteration = 0;

            while (m_Running)
            {
                WorkerAllocStrings(iteration);
                WorkerAllocCollections();

                iteration++;
                Thread.Sleep(k_WorkerSleepMs);
            }
        }

        void WorkerAllocStrings(int iteration)
        {
            for (int i = 0; i < k_WorkerStringCount; i++)
            {
                string result = "Worker_" + iteration + "_item";
                _ = result;
            }
        }

        void WorkerAllocCollections()
        {
            var list = new List<int>(k_WorkerListCapacity);
            for (int i = 0; i < k_WorkerListElements; i++)
                list.Add(i);

            byte[] buffer = new byte[k_WorkerBufferSize];
            _ = buffer;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 2: ThreadPool
        // ═══════════════════════════════════════════════════════════════════

        void PoolWorkItem(object state)
        {
            Dictionary<string, List<int>> dict = BuildPoolDictionary();

            StringBuilder sb = new StringBuilder();
            foreach (var kvp in dict)
            {
                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append(kvp.Key);
            }
            string summary = sb.ToString();
            _ = summary;
        }

        Dictionary<string, List<int>> BuildPoolDictionary()
        {
            var dict = new Dictionary<string, List<int>>(k_PoolDictionaryCapacity);

            for (int i = 0; i < k_PoolDictionaryCapacity; i++)
            {
                string key = "Key_" + i;
                var values = new List<int>(3) { i, i * 2, i * 3 };
                dict.Add(key, values);
            }

            return dict;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pattern 3: Task.Run
        // ═══════════════════════════════════════════════════════════════════

        static void TaskWorkBody(int frame)
        {
            var sb = new StringBuilder(k_TaskStringBuilderCapacity);

            for (int i = 0; i < k_TaskAppendIterations; i++)
                sb.Append("Frame_").Append(frame).Append("_i").Append(i);

            string result = sb.ToString();
            _ = result;

            var payload = new TaskPayload
            {
                Id = frame,
                Data = new byte[k_TaskPayloadBufferSize],
                Tag = "task_" + frame
            };
            _ = payload;
        }
    }
}
