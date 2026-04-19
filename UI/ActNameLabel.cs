using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.addons.mega_text;

namespace dubiousQOL.UI;

/// <summary>
/// Per-act font/color/spacing definition. Keys match ActModel.Id.Entry (lowercased class name).
/// </summary>
internal struct ActStyle
{
    public Color Color;
    public string FontId;
    public int MaxFontSize;
    public int GlyphSpacing;
    public int MarginLeft;
    public int MarginTop;
}

/// <summary>
/// Act-themed styles lookup. Maps act identifier to font, color, and spacing.
/// </summary>
internal static class ActNameStyle
{
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
        return new ActStyle { Color = Colors.White, FontId = "", MaxFontSize = 34 };
    }
}

/// <summary>
/// Shared builders for the per-act styled MegaLabel (Overgrowth / Underdocks /
/// Hive / Glory). Used by the top-bar PatchActNameDisplay and by the run
/// history map viewer (MapHistoryViewer). Pulls the label out of the
/// act_banner scene so font/outline match the game's existing act title.
/// </summary>
internal static class ActNameLabel
{
    public const string DefaultName = "DubiousActNameLabel";

    /// <summary>
    /// Creates a blank MegaLabel by extracting the configured ActName label
    /// from act_banner.tscn. Plain new MegaLabel() would fail _Ready's
    /// AssertThemeFontOverride check.
    /// </summary>
    public static MegaLabel? CreateBlank()
    {
        var template = Utilities.NodeHelper.ExtractFromScene<MegaLabel>(
            SceneHelper.GetScenePath("ui/act_banner"), "ActName");
        if (template == null) return null;

        template.Name = DefaultName;
        template.AutowrapMode = TextServer.AutowrapMode.Off;
        template.MouseFilter = Control.MouseFilterEnum.Ignore;
        return template;
    }

    /// <summary>
    /// Applies per-act font, color, outline, and shadow to a MegaLabel.
    /// actIdEntry is ActModel.Id.Entry. maxFontSizeOverride lets callers
    /// force a specific size; otherwise the per-act default is used.
    /// </summary>
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
        var fill = color.Lerp(Colors.White, 0.15f);
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

    /// <summary>
    /// Per-act spacing offsets (margin_left/top) used by the top-bar wrapper
    /// to tune position next to the boss icon.
    /// </summary>
    public static (int marginLeft, int marginTop) GetMargins(string actIdEntry)
    {
        var s = ActNameStyle.For(actIdEntry);
        return (Math.Max(0, s.MarginLeft), s.MarginTop);
    }
}
