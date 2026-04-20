using System;
using Godot;
using MegaCrit.Sts2.addons.mega_text;

namespace dubiousQOL.UI;

/// <summary>
/// Shared styling factories: StyleBoxFlat, dark panels, dividers, section headers.
/// </summary>
internal static class StyleHelper
{
    /// <summary>
    /// Creates a StyleBoxFlat with uniform corner radius and optional uniform border.
    /// </summary>
    public static StyleBoxFlat MakeStyleBox(Color bg, int cornerRadius = 0,
        Color? borderColor = null, int borderWidth = 0)
    {
        var sb = new StyleBoxFlat { BgColor = bg };
        if (cornerRadius > 0)
        {
            sb.CornerRadiusTopLeft = cornerRadius;
            sb.CornerRadiusTopRight = cornerRadius;
            sb.CornerRadiusBottomLeft = cornerRadius;
            sb.CornerRadiusBottomRight = cornerRadius;
        }
        if (borderColor.HasValue && borderWidth > 0)
        {
            sb.BorderColor = borderColor.Value;
            sb.BorderWidthTop = borderWidth;
            sb.BorderWidthBottom = borderWidth;
            sb.BorderWidthLeft = borderWidth;
            sb.BorderWidthRight = borderWidth;
        }
        return sb;
    }

    /// <summary>
    /// Creates a PanelContainer with a dark rounded-corner background and content margins.
    /// </summary>
    public static PanelContainer CreateDarkPanel(Color bgColor, int cornerRadius = 12,
        float marginH = 16f, float marginV = 14f)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat
        {
            BgColor = bgColor,
            CornerRadiusTopLeft = cornerRadius,
            CornerRadiusTopRight = cornerRadius,
            CornerRadiusBottomLeft = cornerRadius,
            CornerRadiusBottomRight = cornerRadius,
            ContentMarginLeft = marginH,
            ContentMarginRight = marginH,
            ContentMarginTop = marginV,
            ContentMarginBottom = marginV,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    /// <summary>
    /// Creates a horizontal divider line (ColorRect).
    /// </summary>
    public static ColorRect CreateDivider(Color color, float height = 2f)
    {
        return new ColorRect
        {
            CustomMinimumSize = new Vector2(0, height),
            Color = color,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
    }

    /// <summary>
    /// Creates a MegaLabel section header with outline and shadow effects.
    /// </summary>
    public static MegaLabel CreateSectionHeader(string text, Color color, int fontSize = 36,
        int outlineSize = 8)
    {
        var lbl = new MegaLabel();
        lbl.MouseFilter = Control.MouseFilterEnum.Ignore;
        lbl.CustomMinimumSize = new Vector2(0, 52);
        var font = FontHelper.Load("kreon-bold");
        if (font != null) lbl.AddThemeFontOverride("font", font);
        lbl.AddThemeFontSizeOverride("font_size", fontSize);
        lbl.AddThemeColorOverride("font_color", color);
        lbl.AddThemeColorOverride("font_outline_color", new Color(0.3f, 0.23f, 0.132f));
        lbl.AddThemeConstantOverride("outline_size", outlineSize);
        lbl.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.251f));
        lbl.AddThemeConstantOverride("shadow_offset_x", 8);
        lbl.AddThemeConstantOverride("shadow_offset_y", 6);
        lbl.SetTextAutoSize(text);
        return lbl;
    }

    /// <summary>
    /// Creates a smaller sub-section header (encounter-level), plain Label with outline.
    /// </summary>
    public static Label CreateSubSectionHeader(string text, Color color, int fontSize = 26,
        int outlineSize = 3, float minHeight = 38f)
    {
        var lbl = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Left,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var font = FontHelper.Load("kreon-bold");
        if (font != null) lbl.AddThemeFontOverride("font", font);
        lbl.AddThemeFontSizeOverride("font_size", fontSize);
        lbl.AddThemeColorOverride("font_color", color);
        lbl.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
        lbl.AddThemeConstantOverride("outline_size", outlineSize);
        lbl.CustomMinimumSize = new Vector2(0, minHeight);
        return lbl;
    }

    /// <summary>
    /// Creates a small dim text label for mini-section labels.
    /// </summary>
    public static Label CreateDimLabel(string text, Color? color = null, int fontSize = 18,
        float minHeight = 24f)
    {
        var lbl = new Label
        {
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        lbl.AddThemeFontSizeOverride("font_size", fontSize);
        lbl.AddThemeColorOverride("font_color", color ?? Theme.TextDim);
        lbl.CustomMinimumSize = new Vector2(0, minHeight);
        return lbl;
    }

    /// <summary>
    /// Creates a TextureRect with a vertical alpha gradient and clip_children enabled.
    /// Children added to this node are alpha-masked: fully visible in the middle,
    /// fading to transparent over <paramref name="fadePixels"/> at top and bottom.
    /// Matches the game's NScrollableContainer/Mask pattern (GradientTexture2D + ClipOnly).
    /// </summary>
    public static TextureRect CreateScrollFadeMask(float fadePixels = 50f, float totalHeight = 1080f)
    {
        float topEnd = Math.Min(fadePixels / totalHeight, 0.5f);
        float botStart = 1f - topEnd;

        var gradient = new Gradient
        {
            Offsets = new[] { 0f, topEnd, botStart, 1f },
            Colors = new[]
            {
                new Color(1, 1, 1, 0f),
                new Color(1, 1, 1, 1f),
                new Color(1, 1, 1, 1f),
                new Color(1, 1, 1, 0f),
            },
        };

        var tex = new GradientTexture2D
        {
            Gradient = gradient,
            Width = 4,
            Height = Math.Max(1, (int)totalHeight),
            Fill = GradientTexture2D.FillEnum.Linear,
            FillFrom = new Vector2(0, 0),
            FillTo = new Vector2(0, 1),
        };

        var mask = new TextureRect
        {
            Texture = tex,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        mask.ClipChildren = (CanvasItem.ClipChildrenMode)1;
        return mask;
    }
}
