using System;
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

        static readonly string[] k_PoolKeys = { "Key_0", "Key_1", "Key_2", "Key_3" };

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
        //  ThreadStatic Fields (pool / task reuse)
        // ═══════════════════════════════════════════════════════════════════

        [ThreadStatic] static StringBuilder t_PoolSB;
        [ThreadStatic] static Dictionary<string, List<int>> t_PoolDict;
        [ThreadStatic] static StringBuilder t_TaskSB;
        [ThreadStatic] static TaskPayload t_TaskPayload;
        [ThreadStatic] static byte[] t_TaskBuffer;

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
        bool m_Optimized;

        // ═══════════════════════════════════════════════════════════════════
        //  MonoBehaviour Lifecycle
        // ═══════════════════════════════════════════════════════════════════

        void Start()
        {
            m_Optimized = GCAllocTestRig.CurrentMode == AllocMode.Optimized;
            m_Frame = 0;

            if (m_EnableRawThread)
            {
                m_Running = true;
                m_WorkerThread = new Thread(m_Optimized ? WorkerThreadLoopOpt : WorkerThreadLoop)
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
            {
                if (m_Optimized)
                    ThreadPool.QueueUserWorkItem(PoolWorkItemOpt);
                else
                    ThreadPool.QueueUserWorkItem(PoolWorkItem);
            }

            if (m_EnableTaskRun && m_Frame % k_TaskFrameInterval == 0)
            {
                if (m_Optimized)
                {
                    int frameNum = m_Frame;
                    _ = Task.Factory.StartNew(TaskWorkBodyOpt, (object)frameNum);
                }
                else
                {
                    int frameNum = m_Frame;
                    _ = Task.Run(() => TaskWorkBody(frameNum));
                }
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

        // ═══════════════════════════════════════════════════════════════════
        //  Optimized Pattern 1: Raw Thread — reuse buffers
        // ═══════════════════════════════════════════════════════════════════

        void WorkerThreadLoopOpt()
        {
            // Allocate once at thread start, reuse forever
            var sb = new StringBuilder(128);
            var list = new List<int>(k_WorkerListCapacity);
            var buffer = new byte[k_WorkerBufferSize];
            int iteration = 0;

            while (m_Running)
            {
                // String work via SB — no string allocs
                for (int i = 0; i < k_WorkerStringCount; i++)
                {
                    sb.Clear();
                    sb.Append("Worker_").Append(iteration).Append("_item");
                }

                // Reuse list
                list.Clear();
                for (int i = 0; i < k_WorkerListElements; i++)
                    list.Add(i);

                // Touch buffer
                buffer[0] = 0xFF;

                iteration++;
                Thread.Sleep(k_WorkerSleepMs);
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Optimized Pattern 2: ThreadPool — ThreadStatic reuse
        // ═══════════════════════════════════════════════════════════════════

        static void PoolWorkItemOpt(object state)
        {
            if (t_PoolDict == null)
            {
                t_PoolDict = new Dictionary<string, List<int>>(k_PoolDictionaryCapacity);
                for (int i = 0; i < k_PoolDictionaryCapacity; i++)
                    t_PoolDict[k_PoolKeys[i]] = new List<int>(3);
            }
            if (t_PoolSB == null)
                t_PoolSB = new StringBuilder(64);

            // Reuse dict — clear values, refill
            foreach (var kvp in t_PoolDict)
            {
                kvp.Value.Clear();
                int idx = (int)(kvp.Key[4] - '0'); // "Key_N" -> N
                kvp.Value.Add(idx);
                kvp.Value.Add(idx * 2);
                kvp.Value.Add(idx * 3);
            }

            t_PoolSB.Clear();
            bool first = true;
            foreach (var kvp in t_PoolDict)
            {
                if (!first) t_PoolSB.Append(", ");
                t_PoolSB.Append(kvp.Key);
                first = false;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Optimized Pattern 3: Task — ThreadStatic reuse
        // ═══════════════════════════════════════════════════════════════════

        static void TaskWorkBodyOpt(object state)
        {
            int frame = (int)state;

            if (t_TaskSB == null) t_TaskSB = new StringBuilder(k_TaskStringBuilderCapacity);
            if (t_TaskPayload == null) t_TaskPayload = new TaskPayload();
            if (t_TaskBuffer == null) t_TaskBuffer = new byte[k_TaskPayloadBufferSize];

            t_TaskSB.Clear();
            for (int i = 0; i < k_TaskAppendIterations; i++)
                t_TaskSB.Append("Frame_").Append(frame).Append("_i").Append(i);

            t_TaskPayload.Id = frame;
            t_TaskPayload.Data = t_TaskBuffer;
            t_TaskBuffer[0] = 0xFF;

            t_TaskSB.Clear();
            t_TaskSB.Append("task_").Append(frame);
            t_TaskPayload.Tag = t_TaskSB.ToString(); // one small alloc for Tag string
        }
    }
}
