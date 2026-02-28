using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GCAllocBreakdown.Editor
{
    // ═══════════════════════════════════════════════════
    //  CSV EXPORT — standalone static helpers
    // ═══════════════════════════════════════════════════

    internal static class GCAllocExporter
    {
        public static void ExportMarkerTableCSV(List<CallsiteGroup> groups)
        {
            string path = EditorUtility.SaveFilePanel(
                "Export Marker Table CSV", "", "marker-table.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                using (var writer = new StreamWriter(path, false, Encoding.UTF8))
                {
                    writer.WriteLine("Name,Bytes,Count,Avg,Percentage,Median/Frame,Mean/Frame,Min/Frame,Max/Frame,Range/Frame,MinFrame,MaxFrame,FirstFrame");

                    for (int i = 0; i < groups.Count; i++)
                    {
                        var g = groups[i];
                        writer.Write(GCAllocUtils.EscapeCsvField(g.DisplayName));
                        writer.Write(','); writer.Write(g.TotalBytes);
                        writer.Write(','); writer.Write(g.Count);
                        writer.Write(','); writer.Write(g.TotalBytes / Math.Max(1, g.Count));
                        writer.Write(','); writer.Write(g.Percentage.ToString("F2"));
                        writer.Write(','); writer.Write(g.MedianBytesPerFrame);
                        writer.Write(','); writer.Write(g.MeanBytesPerFrame.ToString("F1"));
                        writer.Write(','); writer.Write(g.MinBytesPerFrame);
                        writer.Write(','); writer.Write(g.MaxBytesPerFrame);
                        writer.Write(','); writer.Write(g.RangeBytesPerFrame);
                        writer.Write(','); writer.Write(g.MinFrame);
                        writer.Write(','); writer.Write(g.MaxFrame);
                        writer.Write(','); writer.Write(g.FirstFrame);
                        writer.WriteLine();
                    }
                }

                Debug.Log(string.Concat("[GC Alloc Analyzer] Exported ",
                    groups.Count.ToString(), " markers to ", path));
            }
            catch (Exception e)
            {
                Debug.LogError(string.Concat("[GC Alloc Analyzer] CSV export failed: ", e.Message));
            }
        }

        public static void ExportAllocationsCSV(AnalysisSnapshot snapshot)
        {
            string path = EditorUtility.SaveFilePanel(
                "Export Individual Allocations CSV", "", "allocations.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var allocs = snapshot.RawAllocations;
                using (var writer = new StreamWriter(path, false, Encoding.UTF8))
                {
                    writer.WriteLine("Bytes,Frame,Thread,ParentMethod,HierarchyPath,CallStack");

                    var sb = new StringBuilder(256);
                    for (int i = 0; i < allocs.Count; i++)
                    {
                        var a = allocs[i];
                        writer.Write(a.Bytes);
                        writer.Write(','); writer.Write(GCAllocUtils.DisplayFrame(a.FrameIndex));
                        writer.Write(','); writer.Write(GCAllocUtils.EscapeCsvField(a.ThreadDisplayName));
                        writer.Write(','); writer.Write(GCAllocUtils.EscapeCsvField(a.ParentMethod));
                        writer.Write(','); writer.Write(GCAllocUtils.EscapeCsvField(a.HierarchyPath));
                        writer.Write(',');

                        if (a.ResolvedCallStack != null && a.ResolvedCallStack.Count > 0)
                        {
                            sb.Clear();
                            for (int f = 0; f < a.ResolvedCallStack.Count; f++)
                            {
                                if (f > 0) sb.Append(" > ");
                                sb.Append(a.ResolvedCallStack[f].RawMethodName);
                            }
                            writer.Write(GCAllocUtils.EscapeCsvField(sb.ToString()));
                        }

                        writer.WriteLine();
                    }
                }

                Debug.Log(string.Concat("[GC Alloc Analyzer] Exported ",
                    allocs.Count.ToString(), " allocations to ", path));
            }
            catch (Exception e)
            {
                Debug.LogError(string.Concat("[GC Alloc Analyzer] CSV export failed: ", e.Message));
            }
        }
    }
}
