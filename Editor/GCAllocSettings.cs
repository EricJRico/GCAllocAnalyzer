using System;
using UnityEditor;
using UnityEngine;

namespace GCAllocBreakdown.Editor
{
    // ═══════════════════════════════════════════════════
    //  SEVERITY SETTINGS — user-scoped, EditorPrefs-backed
    // ═══════════════════════════════════════════════════

    internal static class GCAllocSettings
    {
        const string k_KeyPrefix = "GCAllocAnalyzer.";
        const string k_HighColorKey   = k_KeyPrefix + "HighColor";
        const string k_MediumColorKey = k_KeyPrefix + "MediumColor";
        const string k_LowColorKey    = k_KeyPrefix + "LowColor";
        const string k_HighThreshKey  = k_KeyPrefix + "HighThreshold";
        const string k_MedThreshKey   = k_KeyPrefix + "MedThreshold";
        const string k_GraphBarColorKey  = k_KeyPrefix + "GraphBarColor";
        const string k_GraphDimColorKey  = k_KeyPrefix + "GraphDimColor";
        const string k_GraphOverlayColorKey = k_KeyPrefix + "GraphOverlayColor";
        const string k_GraphHighlightTintKey    = k_KeyPrefix + "GraphHighlightTint";
        const string k_GraphHighlightOutlineKey = k_KeyPrefix + "GraphHighlightOutline";
        const string k_GraphBarSpacingKey = k_KeyPrefix + "GraphBarSpacing";

        static readonly Color k_DefaultHigh   = new(1f, 0.3f, 0.3f);
        static readonly Color k_DefaultMedium = new(1f, 0.85f, 0.2f);
        static readonly Color k_DefaultLow    = new(0.7f, 0.7f, 0.7f);
        static readonly Color k_DefaultGraphBar = new(0.25f, 0.60f, 1f);    // matches BarGraphElement default blue
        static readonly Color k_DefaultGraphDim = new(0.12f, 0.16f, 0.22f); // dark muted blue
        static readonly Color k_DefaultGraphOverlay = new(1f, 0.67f, 0f, 0.6f);       // orange, 60% alpha
        static readonly Color k_DefaultGraphHighlightTint = new(1f, 0.78f, 0.2f, 0.35f);  // warm gold, 35% alpha
        static readonly Color k_DefaultGraphHighlightOutline = new(1f, 0.78f, 0.2f, 0.9f); // warm gold, 90% alpha
        const float k_DefaultGraphBarSpacing = 0.12f;                        // matches BarGraphElement default (12%)
        const int k_DefaultHighThreshold = 10240;
        const int k_DefaultMedThreshold  = 1024;

        static Color s_HighColor;
        static Color s_MediumColor;
        static Color s_LowColor;
        static Color s_GraphBarColor;
        static Color s_GraphDimColor;
        static Color s_GraphOverlayColor;
        static Color s_GraphHighlightTint;
        static Color s_GraphHighlightOutline;
        static float s_GraphBarSpacing;
        static int   s_HighThreshold;
        static int   s_MedThreshold;
        static bool  s_Loaded;

        internal static event Action SettingsChanged;

        // ── Public accessors ──

        internal static Color HighColor      { get { EnsureLoaded(); return s_HighColor; } }
        internal static Color MediumColor   { get { EnsureLoaded(); return s_MediumColor; } }
        internal static Color LowColor      { get { EnsureLoaded(); return s_LowColor; } }
        internal static Color GraphBarColor  { get { EnsureLoaded(); return s_GraphBarColor; } }
        internal static Color GraphDimColor  { get { EnsureLoaded(); return s_GraphDimColor; } }
        internal static Color GraphOverlayColor { get { EnsureLoaded(); return s_GraphOverlayColor; } }
        internal static Color GraphHighlightTint { get { EnsureLoaded(); return s_GraphHighlightTint; } }
        internal static Color GraphHighlightOutline { get { EnsureLoaded(); return s_GraphHighlightOutline; } }
        internal static float GraphBarSpacing { get { EnsureLoaded(); return s_GraphBarSpacing; } }

        internal static int HighThreshold { get { EnsureLoaded(); return s_HighThreshold; } }
        internal static int MedThreshold  { get { EnsureLoaded(); return s_MedThreshold; } }

        /// <summary>
        /// Returns the severity color for a given byte count.
        /// Zero-allocation, suitable for hot-path bind callbacks.
        /// </summary>
        internal static Color ColorForBytes(long bytes)
        {
            EnsureLoaded();
            if (bytes >= s_HighThreshold) return s_HighColor;
            if (bytes >= s_MedThreshold)  return s_MediumColor;
            return s_LowColor;
        }

        // ── Setters (save + fire event) ──

        internal static void SetHighColor(Color c)
        {
            s_HighColor = c;
            SaveColor(k_HighColorKey, c);
            SettingsChanged?.Invoke();
        }

        internal static void SetMediumColor(Color c)
        {
            s_MediumColor = c;
            SaveColor(k_MediumColorKey, c);
            SettingsChanged?.Invoke();
        }

        internal static void SetLowColor(Color c)
        {
            s_LowColor = c;
            SaveColor(k_LowColorKey, c);
            SettingsChanged?.Invoke();
        }

        internal static void SetGraphBarColor(Color c)
        {
            s_GraphBarColor = c;
            SaveColor(k_GraphBarColorKey, c);
            SettingsChanged?.Invoke();
        }

        internal static void SetGraphDimColor(Color c)
        {
            s_GraphDimColor = c;
            SaveColor(k_GraphDimColorKey, c);
            SettingsChanged?.Invoke();
        }

        internal static void SetGraphOverlayColor(Color c)
        {
            s_GraphOverlayColor = c;
            SaveColor(k_GraphOverlayColorKey, c);
            SettingsChanged?.Invoke();
        }

        internal static void SetGraphHighlightTint(Color c)
        {
            s_GraphHighlightTint = c;
            SaveColor(k_GraphHighlightTintKey, c);
            SettingsChanged?.Invoke();
        }

        internal static void SetGraphHighlightOutline(Color c)
        {
            s_GraphHighlightOutline = c;
            SaveColor(k_GraphHighlightOutlineKey, c);
            SettingsChanged?.Invoke();
        }

        internal static void SetGraphBarSpacing(float value)
        {
            s_GraphBarSpacing = Mathf.Clamp(value, 0f, 0.5f);
            EditorPrefs.SetFloat(k_GraphBarSpacingKey, s_GraphBarSpacing);
            SettingsChanged?.Invoke();
        }

        internal static void SetHighThreshold(int value)
        {
            s_HighThreshold = Math.Max(value, s_MedThreshold + 1);
            EditorPrefs.SetInt(k_HighThreshKey, s_HighThreshold);
            SettingsChanged?.Invoke();
        }

        internal static void SetMedThreshold(int value)
        {
            s_MedThreshold = Math.Max(value, 0);
            if (s_HighThreshold <= s_MedThreshold)
            {
                s_HighThreshold = s_MedThreshold + 1;
                EditorPrefs.SetInt(k_HighThreshKey, s_HighThreshold);
            }
            EditorPrefs.SetInt(k_MedThreshKey, s_MedThreshold);
            SettingsChanged?.Invoke();
        }

        internal static void ResetToDefaults()
        {
            s_HighColor      = k_DefaultHigh;
            s_MediumColor    = k_DefaultMedium;
            s_LowColor       = k_DefaultLow;
            s_GraphBarColor   = k_DefaultGraphBar;
            s_GraphDimColor   = k_DefaultGraphDim;
            s_GraphOverlayColor = k_DefaultGraphOverlay;
            s_GraphHighlightTint = k_DefaultGraphHighlightTint;
            s_GraphHighlightOutline = k_DefaultGraphHighlightOutline;
            s_GraphBarSpacing = k_DefaultGraphBarSpacing;
            s_HighThreshold  = k_DefaultHighThreshold;
            s_MedThreshold   = k_DefaultMedThreshold;

            SaveColor(k_HighColorKey, s_HighColor);
            SaveColor(k_MediumColorKey, s_MediumColor);
            SaveColor(k_LowColorKey, s_LowColor);
            SaveColor(k_GraphBarColorKey, s_GraphBarColor);
            SaveColor(k_GraphDimColorKey, s_GraphDimColor);
            SaveColor(k_GraphOverlayColorKey, s_GraphOverlayColor);
            SaveColor(k_GraphHighlightTintKey, s_GraphHighlightTint);
            SaveColor(k_GraphHighlightOutlineKey, s_GraphHighlightOutline);
            EditorPrefs.SetFloat(k_GraphBarSpacingKey, s_GraphBarSpacing);
            EditorPrefs.SetInt(k_HighThreshKey, s_HighThreshold);
            EditorPrefs.SetInt(k_MedThreshKey, s_MedThreshold);

            SettingsChanged?.Invoke();
        }

        // ── Persistence helpers ──

        static void EnsureLoaded()
        {
            if (s_Loaded) return;
            s_Loaded = true;

            s_HighColor      = LoadColor(k_HighColorKey, k_DefaultHigh);
            s_MediumColor    = LoadColor(k_MediumColorKey, k_DefaultMedium);
            s_LowColor       = LoadColor(k_LowColorKey, k_DefaultLow);
            s_GraphBarColor  = LoadColor(k_GraphBarColorKey, k_DefaultGraphBar);
            s_GraphDimColor  = LoadColor(k_GraphDimColorKey, k_DefaultGraphDim);
            s_GraphOverlayColor = LoadColor(k_GraphOverlayColorKey, k_DefaultGraphOverlay);
            s_GraphHighlightTint = LoadColor(k_GraphHighlightTintKey, k_DefaultGraphHighlightTint);
            s_GraphHighlightOutline = LoadColor(k_GraphHighlightOutlineKey, k_DefaultGraphHighlightOutline);
            s_GraphBarSpacing = EditorPrefs.GetFloat(k_GraphBarSpacingKey, k_DefaultGraphBarSpacing);
            s_HighThreshold  = EditorPrefs.GetInt(k_HighThreshKey, k_DefaultHighThreshold);
            s_MedThreshold   = EditorPrefs.GetInt(k_MedThreshKey, k_DefaultMedThreshold);
        }

        static Color LoadColor(string key, Color fallback)
        {
            string hex = EditorPrefs.GetString(key, "");
            return ColorUtility.TryParseHtmlString("#" + hex, out Color c) ? c : fallback;
        }

        static void SaveColor(string key, Color c)
        {
            EditorPrefs.SetString(key, ColorUtility.ToHtmlStringRGBA(c));
        }
    }

    // ═══════════════════════════════════════════════════
    //  SETTINGS PROVIDER — Preferences/Analysis/GC Alloc Analyzer
    // ═══════════════════════════════════════════════════

    internal class GCAllocSettingsProvider : SettingsProvider
    {
        GCAllocSettingsProvider()
            : base("Preferences/Analysis/GC Alloc Analyzer", SettingsScope.User) { }

        [SettingsProvider]
        static SettingsProvider CreateProvider() => new GCAllocSettingsProvider
        {
            keywords = new[] { "gc", "alloc", "allocation", "profiler", "severity", "color", "threshold", "overlay" }
        };

        public override void OnGUI(string searchContext)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Severity Colors", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            EditorGUI.BeginChangeCheck();

            Color high   = EditorGUILayout.ColorField("High", GCAllocSettings.HighColor);
            Color medium = EditorGUILayout.ColorField("Medium", GCAllocSettings.MediumColor);
            Color low    = EditorGUILayout.ColorField("Low / Normal", GCAllocSettings.LowColor);

            if (EditorGUI.EndChangeCheck())
            {
                GCAllocSettings.SetHighColor(high);
                GCAllocSettings.SetMediumColor(medium);
                GCAllocSettings.SetLowColor(low);
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Graph Colors", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            EditorGUI.BeginChangeCheck();

            Color barColor     = EditorGUILayout.ColorField("Bar Color", GCAllocSettings.GraphBarColor);
            Color dimColor     = EditorGUILayout.ColorField("Dim Bar Color", GCAllocSettings.GraphDimColor);
            Color overlayColor = EditorGUILayout.ColorField("Overlay Highlight", GCAllocSettings.GraphOverlayColor);
            Color hlTint       = EditorGUILayout.ColorField("Segment Highlight Tint", GCAllocSettings.GraphHighlightTint);
            Color hlOutline    = EditorGUILayout.ColorField("Segment Highlight Outline", GCAllocSettings.GraphHighlightOutline);
            float spacing      = EditorGUILayout.Slider("Bar Spacing", GCAllocSettings.GraphBarSpacing, 0f, 0.5f);

            if (EditorGUI.EndChangeCheck())
            {
                GCAllocSettings.SetGraphBarColor(barColor);
                GCAllocSettings.SetGraphDimColor(dimColor);
                GCAllocSettings.SetGraphOverlayColor(overlayColor);
                GCAllocSettings.SetGraphHighlightTint(hlTint);
                GCAllocSettings.SetGraphHighlightOutline(hlOutline);
                GCAllocSettings.SetGraphBarSpacing(spacing);
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Severity Thresholds", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            EditorGUI.BeginChangeCheck();

            int medThresh  = EditorGUILayout.IntField("Medium (bytes)", GCAllocSettings.MedThreshold);
            int highThresh = EditorGUILayout.IntField("High (bytes)", GCAllocSettings.HighThreshold);

            if (EditorGUI.EndChangeCheck())
            {
                GCAllocSettings.SetMedThreshold(medThresh);
                GCAllocSettings.SetHighThreshold(highThresh);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                $"Allocations \u2265 {GCAllocSettings.HighThreshold:N0} B → High color\n" +
                $"Allocations \u2265 {GCAllocSettings.MedThreshold:N0} B → Medium color\n" +
                $"Below → Low / Normal color",
                MessageType.Info);

            EditorGUILayout.Space(12);

            if (GUILayout.Button("Reset to Defaults", GUILayout.Width(150)))
                GCAllocSettings.ResetToDefaults();
        }
    }
}
