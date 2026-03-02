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

        static readonly Color k_DefaultHigh   = new(1f, 0.3f, 0.3f);
        static readonly Color k_DefaultMedium = new(1f, 0.85f, 0.2f);
        static readonly Color k_DefaultLow    = new(0.7f, 0.7f, 0.7f);
        const int k_DefaultHighThreshold = 10240;
        const int k_DefaultMedThreshold  = 1024;

        static Color s_HighColor;
        static Color s_MediumColor;
        static Color s_LowColor;
        static int   s_HighThreshold;
        static int   s_MedThreshold;
        static bool  s_Loaded;

        internal static event Action SettingsChanged;

        // ── Public accessors ──

        internal static Color HighColor   { get { EnsureLoaded(); return s_HighColor; } }
        internal static Color MediumColor { get { EnsureLoaded(); return s_MediumColor; } }
        internal static Color LowColor    { get { EnsureLoaded(); return s_LowColor; } }

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
            s_HighThreshold  = k_DefaultHighThreshold;
            s_MedThreshold   = k_DefaultMedThreshold;

            SaveColor(k_HighColorKey, s_HighColor);
            SaveColor(k_MediumColorKey, s_MediumColor);
            SaveColor(k_LowColorKey, s_LowColor);
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
            keywords = new[] { "gc", "alloc", "allocation", "profiler", "severity", "color", "threshold" }
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
