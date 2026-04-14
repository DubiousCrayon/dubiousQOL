using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.sts2.Core.Nodes.TopBar;

namespace dubiousQOL.Patches;

internal struct ActStyle
{
    public Color Color;
    public string FontPath;
    public int MaxFontSize;      // per-act max; some custom fonts render smaller and need a boost
    public int GlyphSpacing;     // extra pixels between glyphs (FontVariation.SpacingGlyph)
    public int MarginLeft;       // extra px between boss icon and text (MarginContainer margin_left)
    public int MarginTop;        // px shift; positive pushes text down, negative pulls up
}

internal static class ActNameStyle
{
    // Act-themed colors + fonts. Keys match ActModel.Id.Entry (lowercased class name).
    private static readonly Dictionary<string, ActStyle> Styles = new()
    {
        { "overgrowth", new ActStyle { Color = new Color(0.38f, 0.78f, 0.30f), FontPath = "res://dubiousQOL/fonts/Mighty Souly.otf",     MaxFontSize = 30, GlyphSpacing = 0, MarginLeft = 0,  MarginTop = -6 } },
        { "underdocks", new ActStyle { Color = new Color(0.22f, 0.60f, 0.72f), FontPath = "res://dubiousQOL/fonts/BeachFlower-Bold.otf", MaxFontSize = 34, GlyphSpacing = 0, MarginLeft = 0,  MarginTop = 0 } },
        { "hive",       new ActStyle { Color = new Color(0.98f, 0.75f, 0.20f), FontPath = "res://dubiousQOL/fonts/Kaleo-Regular.ttf",    MaxFontSize = 34, GlyphSpacing = 2, MarginLeft = 0,  MarginTop = 0 } },
        { "glory",      new ActStyle { Color = new Color(0.80f, 0.40f, 0.90f), FontPath = "res://dubiousQOL/fonts/SANDEN.ttf",           MaxFontSize = 30, GlyphSpacing = 0, MarginLeft = 0, MarginTop = 0 } },
    };

    public static ActStyle For(string idEntry)
    {
        if (Styles.TryGetValue(idEntry.ToLowerInvariant(), out var style))
            return style;
        return new ActStyle { Color = Godot.Colors.White, FontPath = "", MaxFontSize = 34 };
    }
}

/// <summary>
/// Shows the current act name (Overgrowth / Underdocks / Hive / Glory) as styled
/// text to the right of the top-bar boss icon. The label is placed as a sibling
/// of BossIcon inside the RoomIcons HBoxContainer so it flows inline with the
/// existing icons instead of overlapping them. Reuses the ActName MegaLabel from
/// the act_banner scene so font/outline match the game's existing act title.
/// </summary>
[HarmonyPatch(typeof(NTopBarBossIcon), "OnActEntered")]
public static class PatchActNameDisplay
{
    private const string LabelName = "DubiousActNameLabel";
    private const string WrapperName = "DubiousActNameWrapper";

    [HarmonyPostfix]
    public static void Postfix(NTopBarBossIcon __instance)
    {
        try
        {
            UpdateLabel(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"ActNameDisplay: {e.Message}");
        }
    }

    private static void UpdateLabel(NTopBarBossIcon host)
    {
        var runState = host._runState;
        if (runState?.Act == null) return;

        var parent = host.GetParent(); // RoomIcons HBox
        if (parent == null) return;

        var wrapper = parent.GetNodeOrNull<MarginContainer>(WrapperName);
        MegaLabel? label;
        if (wrapper == null)
        {
            label = CreateLabel();
            if (label == null) return;
            wrapper = new MarginContainer
            {
                Name = WrapperName,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                SizeFlagsVertical = Control.SizeFlags.Fill,
            };
            wrapper.AddChild(label);
            parent.AddChild(wrapper);
            parent.MoveChild(wrapper, host.GetIndex() + 1);
        }
        else
        {
            label = wrapper.GetNodeOrNull<MegaLabel>(LabelName);
            if (label == null) return;
        }

        var actKey = runState.Act.Id.Entry;
        var title = runState.Act.Title.GetFormattedText();

        var style = ActNameStyle.For(actKey);
        var baseFont = string.IsNullOrEmpty(style.FontPath) ? null
            : ResourceLoader.Load<Font>(style.FontPath, null, ResourceLoader.CacheMode.Reuse);
        if (baseFont != null)
        {
            var variation = new FontVariation { BaseFont = baseFont, SpacingGlyph = style.GlyphSpacing };
            label.AddThemeFontOverride("font", variation);
        }
        label.MaxFontSize = style.MaxFontSize;

        // Per-act spacing tweaks: push text right of boss icon, and vertical nudge.
        wrapper.AddThemeConstantOverride("margin_left", Math.Max(0, style.MarginLeft));
        // Negative margin_top shifts content up without inflating the wrapper's
        // min_size (which would grow the HBox row and push the whole top bar down).
        wrapper.AddThemeConstantOverride("margin_top", style.MarginTop);
        wrapper.AddThemeConstantOverride("margin_bottom", 0);

        var color = style.Color;
        var fill = color.Lerp(Godot.Colors.White, 0.15f);
        var outline = new Color(color.R * 0.18f, color.G * 0.18f, color.B * 0.22f, 1f);
        label.AddThemeColorOverride("font_color", fill);
        label.AddThemeColorOverride("font_outline_color", outline);
        label.AddThemeConstantOverride("outline_size", 12);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.55f));
        label.AddThemeConstantOverride("shadow_offset_x", 2);
        label.AddThemeConstantOverride("shadow_offset_y", 3);
        label.AddThemeConstantOverride("shadow_outline_size", 1);
        label.SetTextAutoSize(title);
    }

    // Pluck the ActName MegaLabel out of act_banner.tscn so we inherit its theme
    // font override and outline — plain `new MegaLabel()` would fail _Ready's
    // AssertThemeFontOverride check.
    private static MegaLabel? CreateLabel()
    {
        var packed = ResourceLoader.Load<PackedScene>(
            SceneHelper.GetScenePath("ui/act_banner"), null, ResourceLoader.CacheMode.Reuse);
        if (packed == null) return null;

        var banner = packed.Instantiate<Node>(PackedScene.GenEditState.Disabled);
        var template = banner.GetNodeOrNull<MegaLabel>("ActName");
        if (template == null)
        {
            banner.QueueFree();
            return null;
        }
        template.GetParent().RemoveChild(template);
        banner.QueueFree();

        template.Name = LabelName;
        template.MinFontSize = 14;
        template.MaxFontSize = 34;
        template.AutowrapMode = TextServer.AutowrapMode.Off;
        template.HorizontalAlignment = HorizontalAlignment.Left;
        template.VerticalAlignment = VerticalAlignment.Center;
        template.MouseFilter = Control.MouseFilterEnum.Ignore;
        // Height fixed so the row doesn't shift; width 0 lets Label.get_minimum_size
        // shrink to the rendered text so the flame badge docks right up against it.
        template.CustomMinimumSize = new Vector2(0, 80);
        template.SizeFlagsVertical = Control.SizeFlags.Fill;
        template.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        return template;
    }
}
