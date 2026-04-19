using Godot;

namespace dubiousQOL.UI.Custom;

/// <summary>
/// Custom-built UI widgets from raw Godot controls with our own styling.
/// Unlike WidgetHelper/TabHelper/CloneHelper (which clone game assets for
/// pixel-perfect matching), these are original constructions for overlays,
/// HUD panels, and other mod-specific UI that has no game equivalent to clone.
/// </summary>
internal static class Widgets
{
    /// <summary>
    /// Applies normal/hover/pressed/focus StyleBoxFlat overrides to a Button
    /// using two colors (normal and hover/pressed). Focus is transparent.
    /// </summary>
    public static void StyleButton(Button btn, Color normal, Color hover)
    {
        btn.AddThemeStyleboxOverride("normal", StyleHelper.MakeStyleBox(normal, 3));
        btn.AddThemeStyleboxOverride("hover", StyleHelper.MakeStyleBox(hover, 3));
        btn.AddThemeStyleboxOverride("pressed", StyleHelper.MakeStyleBox(hover, 3));
        btn.AddThemeStyleboxOverride("focus", StyleHelper.MakeStyleBox(Colors.Transparent));
    }

    /// <summary>
    /// Styles a Button as an active or inactive tab with an accent-colored bottom
    /// border when active. Applies font color overrides for active/inactive/hover states.
    /// </summary>
    public static void StyleTabButton(Button btn, bool active, Color accent)
    {
        var bg = new Color(0.14f, 0.14f, 0.20f, 0.8f);
        var hoverBg = new Color(0.22f, 0.22f, 0.30f, 0.9f);
        btn.AddThemeStyleboxOverride("normal", MakeTabStylebox(bg, active, accent));
        btn.AddThemeStyleboxOverride("hover", MakeTabStylebox(hoverBg, active, accent));
        btn.AddThemeStyleboxOverride("pressed", MakeTabStylebox(hoverBg, active, accent));
        btn.AddThemeStyleboxOverride("focus", StyleHelper.MakeStyleBox(Colors.Transparent));

        var inactiveText = new Color(0.62f, 0.62f, 0.70f);
        btn.AddThemeColorOverride("font_color", active ? accent : inactiveText);
        btn.AddThemeColorOverride("font_hover_color", active ? accent : new Color(0.85f, 0.85f, 0.92f));
        btn.AddThemeColorOverride("font_pressed_color", active ? accent : inactiveText);
    }

    /// <summary>
    /// Creates a small arrow button (e.g. "◀" or "▶") for cycling through options
    /// in a compact selector.
    /// </summary>
    public static Button CreateArrowButton(string text, Color normalColor, Color hoverColor)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(28, 24),
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        btn.AddThemeFontSizeOverride("font_size", 11);
        StyleButton(btn, normalColor, hoverColor);
        return btn;
    }

    /// <summary>
    /// Creates a small toggle-style button for binary options (e.g. "Per Turn" / "Cumulative").
    /// </summary>
    public static Button CreateToggleButton(string label)
    {
        var btn = new Button
        {
            Text = label,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 22),
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        btn.AddThemeFontSizeOverride("font_size", 11);
        return btn;
    }

    /// <summary>
    /// Creates a styled RichTextLabel with font, color, outline, and shadow.
    /// Suitable for overlay text displays (damage numbers, stat readouts).
    /// BbcodeEnabled is on; scroll and autowrap are off; starts hidden.
    /// </summary>
    public static RichTextLabel CreateStyledRichLabel(string name, string? fontId,
        int fontSize, Color color, int outlineSize = 5,
        Color? outlineColor = null, Color? shadowColor = null)
    {
        var lbl = new RichTextLabel
        {
            Name = name,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            BbcodeEnabled = true,
            FitContent = false,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
            Visible = false,
        };
        if (fontId != null)
        {
            var font = FontHelper.Load(fontId);
            if (font != null) lbl.AddThemeFontOverride("normal_font", font);
        }
        lbl.AddThemeFontSizeOverride("normal_font_size", fontSize);
        lbl.AddThemeColorOverride("default_color", color);
        lbl.AddThemeColorOverride("font_outline_color", outlineColor ?? new Color(0f, 0f, 0f));
        lbl.AddThemeConstantOverride("outline_size", outlineSize);
        lbl.AddThemeColorOverride("font_shadow_color", shadowColor ?? new Color(0f, 0f, 0f, 0.7f));
        lbl.AddThemeConstantOverride("shadow_offset_x", 1);
        lbl.AddThemeConstantOverride("shadow_offset_y", 2);
        return lbl;
    }

    /// <summary>
    /// Builds a Control whose child Labels sit on a circular arc, each glyph
    /// rotated so its baseline is tangent to the arc. Used for curved text
    /// effects like badge inscriptions.
    /// </summary>
    public static Control CreateArchLabel(string text, string? fontId, int fontSize,
        Color color, float radius, float arcDegrees, float centerOffsetY = 0f,
        int outlineSize = 6, Color? outlineColor = null, Color? shadowColor = null)
    {
        var container = new Control
        {
            Name = "Arch",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = 0,
            OffsetRight = 0,
            OffsetTop = centerOffsetY,
            OffsetBottom = centerOffsetY,
        };

        Font? font = fontId != null ? FontHelper.Load(fontId) : null;

        int n = text.Length;
        if (n == 0) return container;

        float arcRad = Mathf.DegToRad(arcDegrees);
        float step = n > 1 ? arcRad / (n - 1) : 0;
        float startAngle = -Mathf.Pi / 2f - arcRad / 2f;

        var glyphSize = new Vector2(fontSize + 6, fontSize + 8);

        for (int i = 0; i < n; i++)
        {
            float angle = startAngle + step * i;
            float x = Mathf.Cos(angle) * radius;
            float y = Mathf.Sin(angle) * radius;

            var ch = new Label
            {
                Text = text[i].ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Size = glyphSize,
                PivotOffset = glyphSize / 2f,
                Position = new Vector2(x, y) - glyphSize / 2f,
                Rotation = angle + Mathf.Pi / 2f,
            };
            if (font != null) ch.AddThemeFontOverride("font", font);
            ch.AddThemeFontSizeOverride("font_size", fontSize);
            ch.AddThemeColorOverride("font_color", color);
            ch.AddThemeColorOverride("font_outline_color", outlineColor ?? new Color(0f, 0f, 0f));
            ch.AddThemeConstantOverride("outline_size", outlineSize);
            ch.AddThemeColorOverride("font_shadow_color", shadowColor ?? new Color(0, 0, 0, 0.9f));
            ch.AddThemeConstantOverride("shadow_offset_x", 0);
            ch.AddThemeConstantOverride("shadow_offset_y", 2);
            ch.AddThemeConstantOverride("shadow_outline_size", 3);
            container.AddChild(ch);
        }
        return container;
    }

    private static StyleBoxFlat MakeTabStylebox(Color bg, bool active, Color accent)
    {
        var sb = new StyleBoxFlat { BgColor = bg };
        sb.CornerRadiusTopLeft = 3;
        sb.CornerRadiusTopRight = 3;
        sb.CornerRadiusBottomLeft = 3;
        sb.CornerRadiusBottomRight = 3;
        if (active)
        {
            sb.BorderColor = accent;
            sb.BorderWidthBottom = 2;
        }
        return sb;
    }
}
