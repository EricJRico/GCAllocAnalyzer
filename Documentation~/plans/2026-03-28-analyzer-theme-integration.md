# Plan: Analyzer Theme Integration

Companion to [Plan_LightDarkMode_ColorBlind.md](Plan_LightDarkMode_ColorBlind.md) which covers the BarGraph USS foundation. This plan covers how the GCAllocAnalyzer window consumes those theme capabilities: overflow menu, full window theming, Preferences interaction, and CVD data palette.

## Context

The GCAllocAnalyzer window UI is dark-mode-only. The BarGraph USS plan defines `.bar-graph--dark` / `.bar-graph--light` / `.bar-graph--cvd-*` class selectors for graph chrome. This plan extends theming to the full analyzer window (callstack text, tooltips, toolbar borders, foldout headers) and wires up the user-facing controls.

---

## Architecture

### Two color domains

| Domain | Mechanism | Where defined |
|--------|-----------|---------------|
| **Bar graph chrome** (bg, grid, axes, hover, selection, focus, drag, overlay, labels) | USS class selectors | `BarGraph.uss`, `BarGraphOverviewStrip.uss`, `GCAllocAnalyzer.uss` |
| **Analyzer UI** (callstack text, tooltip, toolbar borders, foldout headers, separators) | Centralized `ThemeColors` static class read at bind-time / construction | New file `Editor/ThemeColors.cs` |

### Theme modes

Three modes in the overflow menu (⋮): **Auto** / **Dark** / **Light**

**Auto** detects `EditorGUIUtility.isProSkin` once at window open / domain reload (no polling).

### Preferences interaction — theme-managed with optional override

Three Preferences colors are theme-sensitive (invisible on the wrong background):

| Setting | Dark default | Light default | Problem |
|---------|-------------|--------------|---------|
| `GraphHighlightOutline` | white 60% | dark 50% | White invisible on light |
| `GraphOverlayColor` | white 100% | USS `--bar-graph-overlay-tint` | White invisible on light |
| `GraphHighlightTint` | gold 35% | gold 30% | Works but suboptimal |

**All three have USS fallbacks** — the C# override can be skipped and USS provides the correct per-theme value:
- `VisTagHighlightTint` → USS `--bar-graph-tag-highlight-tint`
- `VisTagHighlightOutline` → USS `--bar-graph-tag-highlight-outline`
- `SetOverlay(null)` → USS `--bar-graph-overlay-tint`

**Behavior:**
- **At default** — Preferences value matches either the dark or light default (compared as `Color32`) → don't set C# override, let USS handle per-theme. Shows "(auto)" in Preferences panel.
- **Customized** — value doesn't match any default → set C# override with user's value. Persists through theme switches.
- **"Reset to Theme Defaults" button** — resets all three to current theme's defaults.

**Pre-requisite fix:** `GCAllocAnalyzer.uss` has `--bar-graph-tag-highlight-outline: rgba(255, 200, 50, 0.9)` (gold) but the C# default is `(1, 1, 1, 0.6)` (white). Users have always seen white. Align USS dark value to white before relying on USS.

The remaining Preferences colors (GraphBarColor, GraphDimColor, severity colors, bar spacing) are safe on both backgrounds and always come from Preferences.

### Settings flow

```
Overflow menu (IHasCustomMenu)
    → GCAllocSettings.ThemeMode / CvdMode setter
        → ThemeColors.Resolve() — caches all color values, resolves s_IsDark
        → GCAllocSettings.SettingsChanged event fires
            → GCAllocAnalyzerWindow.OnSettingsChanged() → RefreshTheme() + RefreshItems()
            → PerFrameGraphController.ApplySettings() → ApplyThemeClasses()
            → GCAllocBreakdownModule — re-binds cells with new ThemeColors values
```

### USS cascade

Theme classes applied to the `BarGraphElement` and overview strip. CVD classes applied AFTER theme classes (declaration order breaks specificity ties). Analyzer-specific overrides in `GCAllocAnalyzer.uss` use compound selectors (`.gc-alloc-bar-graph.bar-graph--light`).

The overview strip is a **sibling** of the bar graph, not a descendant — needs its own theme class on both the strip element and its private `_innerChart`. Add `AddInnerClass()` / `RemoveInnerClass()` to `BarGraphOverviewStrip`.

---

## Steps

### Step 1: Settings persistence
**File:** `Editor/GCAllocSettings.cs`

- Add `ThemeMode` enum: `Auto = 0, Dark = 1, Light = 2`
- Add `CvdMode` enum: `None = 0, Deuteranopia = 1, Protanopia = 2, Tritanopia = 3`
- Add EditorPrefs keys, static properties with getters/setters that fire `SettingsChanged`
- Wire into `EnsureLoaded()` and `ResetToDefaults()`
- Add `IsThemeDefault(Color32 value, Color32 darkDefault, Color32 lightDefault)` helper
- Add per-color dark/light default constants for the 3 theme-sensitive colors
- Add `ResetHighlightsToThemeDefaults()` method

### Step 2: ThemeColors static class
**File:** `Editor/ThemeColors.cs` (NEW, ~130 lines)

Replaces scattered `static readonly Color k_*` constants across `GCAllocAnalyzerWindow.cs`, `PerFrameGraphController.cs`, and `GCAllocBreakdownModule.cs`.

State:
- `static bool s_IsDark` — resolved from ThemeMode + isProSkin
- `static bool s_CvdActive` — true when CvdMode != None
- `static readonly Color[] k_OkabeIto` — 8-color CVD-safe palette (always allocated)

Color table (cached on `Resolve()`):

| Name | Dark | Light |
|------|------|-------|
| `TopFrame` | `(0.9, 0.9, 0.6)` | `(0.55, 0.45, 0.0)` |
| `CallerFrame` | `(0.55, 0.55, 0.55)` | `(0.35, 0.35, 0.35)` |
| `SubtleText` | `(0.5, 0.5, 0.5)` | `(0.4, 0.4, 0.4)` |
| `HoverBg` | `(0.3, 0.3, 0.3)` | `(0.85, 0.85, 0.85)` |
| `DimGray` | `(0.7, 0.7, 0.7)` | `(0.4, 0.4, 0.4)` |
| `TooltipBg` | `(0.12, 0.12, 0.12)` | `(1.0, 1.0, 1.0)` |
| `TooltipBorder` | `(0.4, 0.4, 0.4)` | `(0.7, 0.7, 0.7)` |
| `TooltipText` | `(1.0, 1.0, 1.0)` | `(0.1, 0.1, 0.1)` |
| `SectionBorder` | `(0.2, 0.2, 0.2)` | `(0.78, 0.78, 0.78)` |
| `HeaderBg` | `(0.25, 0.25, 0.25)` | `(0.88, 0.88, 0.88)` |
| `HeaderBorder` | `(0.15, 0.15, 0.15)` | `(0.72, 0.72, 0.72)` |
| `WarningText` | `(1.0, 0.7, 0.2)` | `(0.75, 0.45, 0.0)` |
| `PanelBg` | `(0.22, 0.22, 0.22)` | `(0.76, 0.76, 0.76)` |

Public API:
- `Resolve(ThemeMode, CvdMode)` — resolves `s_IsDark`, caches colors, sets `s_CvdActive`
- `IsDark`, `IsCvdActive` — booleans (no null checks)
- `OkabeIto` — readonly Color[] (always allocated)
- `CvdClass` — USS class string when active, empty string when not

### Step 3: Fix USS/C# mismatch + light overrides
**File:** `Editor/Resources/GCAllocAnalyzer.uss`

Align outline to match current visual (white, not gold), add light override:
```css
.gc-alloc-bar-graph {
    --bar-graph-tag-highlight-tint: rgba(255, 199, 51, 0.35);
    --bar-graph-tag-highlight-outline: rgba(255, 255, 255, 0.6);  /* was gold, now white */
    --bar-graph-tag-highlight-outline-width: 1.5;
}
.gc-alloc-bar-graph.bar-graph--light {
    --bar-graph-tag-highlight-tint: rgba(200, 150, 20, 0.30);
    --bar-graph-tag-highlight-outline: rgba(0, 0, 0, 0.5);
}
```

### Step 4: USS theme selectors — BarGraph + OverviewStrip
**Files:** `Editor/BarChart/Resources/BarGraph.uss`, `Editor/BarChart/Resources/BarGraphOverviewStrip.uss`

(Covered by the companion plan — dark/light/CVD selectors)

### Step 5: BarGraphOverviewStrip inner class API
**File:** `Editor/BarChart/Core/BarGraphOverviewStrip.cs`

```csharp
public void AddInnerClass(string cls) => _innerChart.AddToClassList(cls);
public void RemoveInnerClass(string cls) => _innerChart.RemoveFromClassList(cls);
```

### Step 6: PerFrameGraphController — theme wiring
**File:** `Editor/PerFrameGraphController.cs`

- Replace `k_DimGray`, `k_TooltipBg`, `k_TooltipBorder` with `ThemeColors.*`
- Replace tooltip `color = Color.white` with `ThemeColors.TooltipText`
- Add `ApplyThemeClasses()` — swap theme/CVD classes on bar graph, overview strip, and inner chart
- In `ApplySettings()`: set highlight C# overrides only when Preferences value is NOT a theme default; pass `null` to `SetOverlay()` when at default
- Add `RefreshTheme()` to re-apply tooltip and label colors

### Step 7: GCAllocAnalyzerWindow — overflow menu + theme wiring
**File:** `Editor/GCAllocAnalyzerWindow.cs`

- Add `IHasCustomMenu`, implement `AddItemsToMenu(GenericMenu menu)`:
  - Theme submenu: Auto (match Unity) / Dark / Light
  - Color Blind Mode submenu: None / Deuteranopia / Protanopia / Tritanopia
- Replace `k_TopFrame`, `k_CallerFrame`, `k_SubtleText`, `k_HoverBg` with `ThemeColors.*`
- Replace inline border/separator colors with `ThemeColors.SectionBorder`
- Replace foldout header colors with `ThemeColors.HeaderBg` / `ThemeColors.HeaderBorder`
- Call `ThemeColors.Resolve()` in `CreateGUI()`
- Extend `OnSettingsChanged()` with `RefreshTheme()` + `RefreshItems()`

### Step 8: GCAllocBreakdownModule — theme wiring
**File:** `Editor/GCAllocBreakdownModule.cs`

- Replace `k_DimGray`, `k_TopFrame`, `k_CallerFrame` with `ThemeColors.*`
- Replace warning color with `ThemeColors.WarningText`, background with `ThemeColors.PanelBg`

### Step 9: CVD-safe data palette
**File:** `Editor/GCAllocAnalyzerData.cs`

```csharp
if (ThemeColors.IsCvdActive && methodIndex != k_OthersIndex)
    return ThemeColors.OkabeIto[methodIndex % ThemeColors.OkabeIto.Length];
```

### Step 10: Preferences panel
**File:** `Editor/GCAllocSettings.cs` (`GCAllocSettingsProvider`)

- Add Theme Mode and CVD Mode dropdowns
- Annotate theme-sensitive colors with "(auto)" when at default
- Add "Reset Highlights to Theme Defaults" button

---

## Files Summary

| File | Action | Est. lines |
|------|--------|-----------|
| `Editor/GCAllocSettings.cs` | MODIFY | +65 |
| `Editor/ThemeColors.cs` | NEW | ~130 |
| `Editor/Resources/GCAllocAnalyzer.uss` | MODIFY | +12 |
| `Editor/BarChart/Core/BarGraphOverviewStrip.cs` | MODIFY | +2 |
| `Editor/PerFrameGraphController.cs` | MODIFY | +40 |
| `Editor/GCAllocAnalyzerWindow.cs` | MODIFY | +45 |
| `Editor/GCAllocBreakdownModule.cs` | MODIFY | +10 |
| `Editor/GCAllocAnalyzerData.cs` | MODIFY | +3 |

USS changes to `BarGraph.uss` and `BarGraphOverviewStrip.uss` are covered by the companion plan.

---

## Verification

1. Dark mode unchanged — identical to current
2. Light mode — readable chrome, sufficient label contrast, no invisible elements
3. Auto mode — Pro skin → dark, Personal skin → light (detected at window open)
4. Toggle mid-session — all chrome updates, no stale colors
5. CVD modes — selection rim and focus rim distinguishable under simulated CVD
6. CVD data palette — Okabe-Ito colors on bars when active, standard palette when off
7. Class composition — light + CVD both apply correctly
8. Domain reload — theme/CVD persist via EditorPrefs
9. Overview strip — themes correctly alongside main graph
10. Profiler module — callstack text uses themed colors
11. Highlight auto/override — at-default colors switch with theme; customized persist
12. Reset button — restores auto behavior
13. Build passes
