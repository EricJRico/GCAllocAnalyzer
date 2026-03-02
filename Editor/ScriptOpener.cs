using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditorInternal;

namespace GCAllocBreakdown.Editor
{
    // ═══════════════════════════════════════════════════
    //  SCRIPT OPENER — resolves MonoScript from method
    //  names or source files and opens them in the IDE
    // ═══════════════════════════════════════════════════

    internal class ScriptOpener
    {
        readonly Dictionary<string, (MonoScript script, bool found)> m_ScriptCache = new();

        // ═══════════════════════════════════════════════════
        //  PUBLIC API
        // ═══════════════════════════════════════════════════

        public bool CanOpen(ResolvedFrame frame)
        {
            if (!string.IsNullOrEmpty(frame.SourceFile))
                return FindScript(Path.GetFileNameWithoutExtension(frame.SourceFile)) != null;

            return FindScriptFromMethodName(frame.RawMethodName) != null;
        }

        public void Open(ResolvedFrame frame)
        {
            MonoScript script = null;
            int line = 1;

            if (!string.IsNullOrEmpty(frame.SourceFile))
            {
                script = FindScript(Path.GetFileNameWithoutExtension(frame.SourceFile));
                if (frame.SourceLine > 0) line = frame.SourceLine;
            }

            if (script == null)
            {
                script = FindScriptFromMethodName(frame.RawMethodName);
                if (script == null) return;

                // Try to find the method in the source text to jump to the right line
                if (frame.SourceLine <= 0)
                {
                    string methodName = ExtractMethodNameFromEnd(frame.RawMethodName);
                    if (methodName.Length > 0)
                    {
                        // Strip generic marker
                        int bt = methodName.IndexOf('`');
                        if (bt >= 0) methodName = methodName.Substring(0, bt);

                        string text = script.text;
                        int idx = text.IndexOf(methodName, StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            int lineCount = 1;
                            for (int c = 0; c < idx; c++)
                                if (text[c] == '\n') lineCount++;
                            line = lineCount;
                        }
                    }
                }
                else
                    line = frame.SourceLine;
            }

            if (script != null)
                AssetDatabase.OpenAsset(script, line);
        }

        public void ClearCache()
        {
            m_ScriptCache.Clear();
        }

        // ═══════════════════════════════════════════════════
        //  SCRIPT RESOLUTION
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Walk backwards through the segments of a method name to find a matching script.
        /// For IL2CPP (::), the class name is always the segment immediately before "::".
        /// For Mono (.), splits by '.' and walks backwards from second-to-last.
        /// Also handles nested classes "+" and generics "`".
        /// </summary>
        MonoScript FindScriptFromMethodName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            // Check cache first with full raw name as key
            if (m_ScriptCache.TryGetValue(raw, out var cached)) return cached.script;

            // Strip assembly prefix
            string clean = GCAllocUtils.StripAssembly(raw);

            // Strip arguments
            int paren = clean.IndexOf('(');
            if (paren >= 0) clean = clean.Substring(0, paren).TrimEnd();

            // Handle IL2CPP "::" — format is Namespace::Class.Method
            // Class name is the first segment AFTER ::
            int dcolon = clean.IndexOf("::", StringComparison.Ordinal);
            if (dcolon >= 0)
            {
                string afterDcolon = clean.Substring(dcolon + 2);

                // afterDcolon is "Class.Method" or just "Method"
                // Walk backwards: last segment is method, everything before is class candidates
                string[] parts = afterDcolon.Split('.');

                // Walk backwards from second-to-last (skip method at end)
                for (int i = parts.Length - 2; i >= 0; i--)
                {
                    string candidate = parts[i];

                    int plus = candidate.IndexOf('+');
                    if (plus >= 0) candidate = candidate.Substring(0, plus);
                    int backtick = candidate.IndexOf('`');
                    if (backtick >= 0) candidate = candidate.Substring(0, backtick);

                    if (candidate.Length == 0) continue;

                    var script = FindScript(candidate);
                    if (script != null)
                    {
                        m_ScriptCache[raw] = (script, true);
                        return script;
                    }
                }

                m_ScriptCache[raw] = (null, false);
                return null;
            }

            // Mono style: "Namespace.ClassName.MethodName" — last is method, walk backwards for class
            int lastDotMono = clean.LastIndexOf('.');
            if (lastDotMono <= 0)
            {
                m_ScriptCache[raw] = (null, false);
                return null;
            }

            string typeSide = clean.Substring(0, lastDotMono);
            string[] segments = typeSide.Split('.');

            // Walk backwards — last segment is most likely the class
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                string candidate = segments[i];

                // Handle nested class: take part before '+'
                int p = candidate.IndexOf('+');
                if (p >= 0) candidate = candidate.Substring(0, p);

                // Handle generics: strip '`1'
                int bt = candidate.IndexOf('`');
                if (bt >= 0) candidate = candidate.Substring(0, bt);

                if (candidate.Length == 0) continue;

                var script = FindScript(candidate);
                if (script != null)
                {
                    m_ScriptCache[raw] = (script, true);
                    return script;
                }
            }

            // Also try nested class parts after '+'
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                int p = segments[i].IndexOf('+');
                if (p < 0) continue;

                string nested = segments[i].Substring(p + 1);
                int bt = nested.IndexOf('`');
                if (bt >= 0) nested = nested.Substring(0, bt);

                if (nested.Length == 0) continue;

                var script = FindScript(nested);
                if (script != null)
                {
                    m_ScriptCache[raw] = (script, true);
                    return script;
                }
            }

            m_ScriptCache[raw] = (null, false);
            return null;
        }

        MonoScript FindScript(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (m_ScriptCache.TryGetValue(name, out var cached)) return cached.script;

            string[] guids = AssetDatabase.FindAssets(string.Concat("t:MonoScript ", name));
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (Path.GetFileNameWithoutExtension(path) != name) continue;

                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                m_ScriptCache[name] = (script, script != null);
                return script;
            }

            m_ScriptCache[name] = (null, false);
            return null;
        }

        // ═══════════════════════════════════════════════════
        //  METHOD NAME EXTRACTION
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Extracts just the method name (last segment) for line searching.
        /// </summary>
        static string ExtractMethodNameFromEnd(string raw)
        {
            string clean = GCAllocUtils.StripAssembly(raw);
            int paren = clean.IndexOf('(');
            if (paren >= 0) clean = clean.Substring(0, paren).TrimEnd();

            // IL2CPP: Namespace::Class.Method — method is last segment after ::
            int dcolon = clean.IndexOf("::", StringComparison.Ordinal);
            int lastDot = 0;
            if (dcolon >= 0)
            {
                string afterDcolon = clean.Substring(dcolon + 2);
                lastDot = afterDcolon.LastIndexOf('.');
                return lastDot >= 0 ? afterDcolon.Substring(lastDot + 1) : afterDcolon;
            }

            // Mono: after last '.'
            lastDot = clean.LastIndexOf('.');
            return lastDot >= 0 ? clean.Substring(lastDot + 1) : clean;
        }
    }
}
