using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace dubiousQOL.UI;

/// <summary>
/// Reusable tab acquisition, cloning, and tab bar construction.
/// Clones the game's NSettingsTab for pixel-perfect styling.
/// </summary>
internal static class TabHelper
{
    private static Node? _cachedSourceTab;
    private static bool _acquired;

    /// <summary>
    /// Finds a tab node from the game's settings or stats screen for cloning.
    /// Caches the result across calls. Returns null if no source found.
    /// </summary>
    public static Node? AcquireSourceTab()
    {
        if (_acquired) return _cachedSourceTab;
        _acquired = true;

        try
        {
            var settingsPath = SceneHelper.GetScenePath("screens/settings_screen/settings_screen");
            if (TryExtract(settingsPath, "SettingsTabManager/General")) return _cachedSourceTab;

            var statsPath = SceneHelper.GetScenePath("screens/stats_screen/stats_screen");
            if (TryExtract(statsPath, "Tabs")) return _cachedSourceTab;
        }
        catch (Exception e) { MainFile.Logger.Warn($"TabHelper acquire: {e.Message}"); }

        return _cachedSourceTab;
    }

    private static bool TryExtract(string scenePath, string tabManagerPath)
    {
        try
        {
            if (!ResourceLoader.Exists(scenePath)) return false;
            var scene = PreloadManager.Cache.GetScene(scenePath);
            var tmp = scene.Instantiate<Node>(PackedScene.GenEditState.Disabled);
            try
            {
                var tabMgr = tmp.GetNodeOrNull(tabManagerPath);
                if (tabMgr == null) return false;
                var tab = FindFirstTab(tabMgr);
                if (tab == null) return false;
                tab.GetParent()?.RemoveChild(tab);
                _cachedSourceTab = tab;
                return true;
            }
            finally { tmp.QueueFree(); }
        }
        catch { return false; }
    }

    private static Node? FindFirstTab(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child.HasMethod("SetLabel")) return child;
            var nested = FindFirstTab(child);
            if (nested != null) return nested;
        }
        return null;
    }

    /// <summary>
    /// Clones a single tab from the acquired source. Handles Duplicate(Full)
    /// + material re-clone for independent HSV hover state.
    /// Falls back to a plain styled Button if no source tab is available.
    /// </summary>
    public static Node CreateTab(string label)
    {
        var source = AcquireSourceTab();
        if (source != null)
        {
            var tab = CloneHelper.CloneWithMaterial<Node>(source, CloneHelper.Full, "TabImage");
            if (tab != null)
            {
                tab.CallDeferred("SetLabel", label);
                return tab;
            }
        }

        // Fallback: plain styled button.
        var btn = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(160, 56),
            FocusMode = Control.FocusModeEnum.All,
        };
        btn.AddThemeFontSizeOverride("font_size", 22);
        return btn;
    }

    /// <summary>
    /// Wires initial selection state on a tab node after _Ready fires.
    /// Sets outline visibility, label modulate (cream/half-transparent-cream),
    /// and _isSelected internal field.
    /// </summary>
    public static void SetInitialState(Node tab, bool selected)
    {
        tab.Ready += () =>
        {
            var outline = tab.GetNodeOrNull<TextureRect>("Outline");
            if (outline != null) outline.Visible = selected;
            var lbl = tab.GetNodeOrNull<Control>("Label");
            if (lbl != null) lbl.Modulate = selected ? Theme.TabActive : Theme.TabInactive;
            tab.Set("_isSelected", selected);
        };
    }

    /// <summary>
    /// Builds a tab bar with the given labels.
    /// tabsPerRow = 0 means single row (all tabs in one HBox).
    /// tabsPerRow > 0 wraps into multiple rows.
    /// Returns the container and the list of created tab nodes.
    /// </summary>
    public static (Control container, List<Node> tabs) CreateTabBar(
        string[] labels, int tabsPerRow = 0, int separation = 12)
    {
        var tabs = new List<Node>();

        if (tabsPerRow > 0 && labels.Length > tabsPerRow)
        {
            // Multi-row layout
            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 8);

            HBoxContainer? currentRow = null;
            for (int i = 0; i < labels.Length; i++)
            {
                if (i % tabsPerRow == 0)
                {
                    currentRow = new HBoxContainer();
                    currentRow.AddThemeConstantOverride("separation", separation);
                    currentRow.Alignment = BoxContainer.AlignmentMode.Center;
                    vbox.AddChild(currentRow);
                }

                var tab = CreateTab(labels[i]);
                tab.Name = $"Tab_{i}";
                currentRow!.AddChild(tab);
                SetInitialState(tab, i == 0);
                tabs.Add(tab);
            }
            return (vbox, tabs);
        }
        else
        {
            // Single row layout
            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", separation);
            hbox.Alignment = BoxContainer.AlignmentMode.Center;

            for (int i = 0; i < labels.Length; i++)
            {
                var tab = CreateTab(labels[i]);
                tab.Name = $"Tab_{i}";
                hbox.AddChild(tab);
                SetInitialState(tab, i == 0);
                tabs.Add(tab);
            }
            return (hbox, tabs);
        }
    }
}
