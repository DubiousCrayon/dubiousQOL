using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.sts2.Core.Nodes.TopBar;

using dubiousQOL.UI;

namespace dubiousQOL.Patches;

internal struct ActStyle
{
    public Color Color;
    public string FontId;        // FontHelper identifier (e.g., "mighty-souly")
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
        { "overgrowth", new ActStyle { Color = new Color(0.38f, 0.78f, 0.30f), FontId = "mighty-souly",  MaxFontSize = 30, GlyphSpacing = 0, MarginLeft = 0,  MarginTop = -6 } },
        { "underdocks", new ActStyle { Color = new Color(0.22f, 0.60f, 0.72f), FontId = "beach-flower",  MaxFontSize = 34, GlyphSpacing = 0, MarginLeft = 0,  MarginTop = 0 } },
        { "hive",       new ActStyle { Color = new Color(0.98f, 0.75f, 0.20f), FontId = "kaleo",         MaxFontSize = 34, GlyphSpacing = 2, MarginLeft = 0,  MarginTop = 0 } },
        { "glory",      new ActStyle { Color = new Color(0.80f, 0.40f, 0.90f), FontId = "sanden",        MaxFontSize = 30, GlyphSpacing = 0, MarginLeft = 0, MarginTop = 0 } },
    };

    public static ActStyle For(string idEntry)
    {
        if (Styles.TryGetValue(idEntry.ToLowerInvariant(), out var style))
            return style;
        return new ActStyle { Color = Godot.Colors.White, FontId = "", MaxFontSize = 34 };
    }
}

/// <summary>
/// Shared builders for the per-act styled MegaLabel (Overgrowth / Underdocks /
/// Hive / Glory). Used by the top-bar PatchActNameDisplay below and by the run
/// history map viewer (MapHistoryViewer). Pulls the label out of the
/// act_banner scene so font/outline match the game's existing act title.
/// </summary>
internal static class ActNameLabel
{
    public const string DefaultName = "DubiousActNameLabel";

    // Plain `new MegaLabel()` would fail _Ready's AssertThemeFontOverride check —
    // pluck the configured ActName label out of act_banner.tscn and detach it.
    public static MegaLabel? CreateBlank()
    {
        var packed = ResourceLoader.Load<PackedScene>(
            SceneHelper.GetScenePath("ui/act_banner"), null, ResourceLoader.CacheMode.Reuse);
        if (packed == null) return null;

        var banner = packed.Instantiate<Node>(PackedScene.GenEditState.Disabled);
        var template = banner.GetNodeOrNull<MegaLabel>("ActName");
        if (template == null) { banner.QueueFree(); return null; }
        template.GetParent().RemoveChild(template);
        banner.QueueFree();

        template.Name = DefaultName;
        template.AutowrapMode = TextServer.AutowrapMode.Off;
        template.MouseFilter = Control.MouseFilterEnum.Ignore;
        return template;
    }

    // Apply per-act font, color, outline, shadow. actIdEntry is ActModel.Id.Entry.
    // maxFontSizeOverride lets callers force a specific size; otherwise the
    // per-act default from ActNameStyle is used.
    public static void ApplyStyle(MegaLabel label, string actIdEntry, string title, int? maxFontSizeOverride = null)
    {
        var style = ActNameStyle.For(actIdEntry);
        var baseFont = string.IsNullOrEmpty(style.FontId) ? null
            : FontHelper.Load(style.FontId);
        if (baseFont != null)
        {
            var variation = new FontVariation { BaseFont = baseFont, SpacingGlyph = style.GlyphSpacing };
            label.AddThemeFontOverride("font", variation);
        }
        label.MaxFontSize = maxFontSizeOverride ?? style.MaxFontSize;

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

    // Per-act spacing offsets (margin_left/top) used by the top-bar wrapper to
    // tune position next to the boss icon. Exposed so callers can apply them
    // without re-reading ActNameStyle.
    public static (int marginLeft, int marginTop) GetMargins(string actIdEntry)
    {
        var s = ActNameStyle.For(actIdEntry);
        return (Math.Max(0, s.MarginLeft), s.MarginTop);
    }
}

/// <summary>
/// Shows the current act name (Overgrowth / Underdocks / Hive / Glory) as styled
/// text to the right of the top-bar boss icon. The label is placed as a sibling
/// of BossIcon inside the RoomIcons HBoxContainer so it flows inline with the
/// existing icons instead of overlapping them.
/// </summary>
[HarmonyPatch(typeof(NTopBarBossIcon), "OnActEntered")]
public static class PatchActNameDisplay
{
    private const string WrapperName = "DubiousActNameWrapper";

    [HarmonyPostfix]
    public static void Postfix(NTopBarBossIcon __instance)
    {
        if (!ActNameDisplayConfig.Instance.Enabled) return;
        try { UpdateLabel(__instance); }
        catch (Exception e) { MainFile.Logger.Warn($"ActNameDisplay: {e.Message}"); }
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
            label = ActNameLabel.CreateBlank();
            if (label == null) return;
            // Top-bar specific sizing: fixed row height, shrink-to-text width
            // so the flame badge docks against the rendered glyphs.
            label.MinFontSize = 14;
            label.MaxFontSize = 34;
            label.HorizontalAlignment = HorizontalAlignment.Left;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.CustomMinimumSize = new Vector2(0, 80);
            label.SizeFlagsVertical = Control.SizeFlags.Fill;
            label.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
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
            label = wrapper.GetNodeOrNull<MegaLabel>(ActNameLabel.DefaultName);
            if (label == null) return;
        }

        var actKey = runState.Act.Id.Entry;
        var title = runState.Act.Title.GetFormattedText();

        // Per-act spacing tweaks: push text right of boss icon, and vertical nudge.
        // Negative margin_top shifts content up without inflating the wrapper's
        // min_size (which would grow the HBox row and push the whole top bar down).
        var (marginLeft, marginTop) = ActNameLabel.GetMargins(actKey);
        wrapper.AddThemeConstantOverride("margin_left", marginLeft);
        wrapper.AddThemeConstantOverride("margin_top", marginTop);
        wrapper.AddThemeConstantOverride("margin_bottom", 0);

        ActNameLabel.ApplyStyle(label, actKey, title);
    }
}
