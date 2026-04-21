using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

using dubiousQOL.UI;
using ModTheme = dubiousQOL.UI.Theme;

namespace dubiousQOL.Patches;

internal partial class IntentPatternsViewer : Control, IScreenContext
{
    public Control? DefaultFocusedControl => null;

    private static Font? _kreonFont;

    private readonly string _title;
    private readonly List<MonsterSection> _sections;

    // Column sizing
    private const float IntentColWidth = 90f;
    private const float ColumnPadding = 20f;
    private const int InlineIconWidth = 18;
    private const int IntentIconSize = 32;
    private const int FontSizeLabel = 20;
    private const int FontSizeEffect = 18;
    private const int FontSizeHeader = 16;
    private const float CardMargins = 70f; // panel margin + hbox separations + row padding
    private const int PortraitSize = 40;

    public IntentPatternsViewer(string creatureName, List<ResolvedPattern> patterns, string monsterEntry)
        : this(creatureName, new List<MonsterSection>
        {
            new() { Name = creatureName, MonsterEntry = monsterEntry, Patterns = patterns }
        })
    {
    }

    public IntentPatternsViewer(string title, List<MonsterSection> sections)
    {
        _title = title;
        _sections = sections;
        Name = "DubiousIntentPatterns";
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.FullRect);

        Build();
    }

    public override void _Ready()
    {
        if (ModalHelper.CreateBackButton(this, "DubiousIntentPatternsBackButton") == null)
            AddChild(ModalHelper.CreateFallbackCloseButton());
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        ModalHelper.TryHandleEscape(inputEvent, this);
    }

    private void Build()
    {
        if (_kreonFont == null || !GodotObject.IsInstanceValid(_kreonFont))
            _kreonFont = FontHelper.Load("kreon-bold");

        // Compute dynamic column widths from content
        float nameColWidth = MeasureNameColumnWidth();
        float effectColWidth = MeasureEffectColumnWidth();
        float cardWidth = nameColWidth + IntentColWidth + effectColWidth + CardMargins;

        // Dim backdrop — click to dismiss
        var backdrop = new ColorRect
        {
            Color = ModTheme.Backdrop,
            MouseFilter = MouseFilterEnum.Stop,
        };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        backdrop.GuiInput += inputEvent =>
        {
            if (inputEvent is InputEventMouseButton mb && mb.Pressed)
            {
                NModalContainer.Instance?.Clear();
                GetViewport().SetInputAsHandled();
            }
        };
        AddChild(backdrop);

        // Card panel positioned near the creature
        var outerPanel = StyleHelper.CreateDarkPanel(ModTheme.PanelBgDark, cornerRadius: 12, marginH: 12f, marginV: 12f);
        outerPanel.MouseFilter = MouseFilterEnum.Stop;
        PositionCard(outerPanel, cardWidth);
        AddChild(outerPanel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        outerPanel.AddChild(vbox);

        bool multiMonster = _sections.Count > 1;

        if (multiMonster)
        {
            // Top-level title for the encounter
            var titleHeader = StyleHelper.CreateSectionHeader(_title, ModTheme.SectionHeader, fontSize: 26, outlineSize: 5);
            titleHeader.AutoSizeEnabled = false;
            titleHeader.AddThemeFontSizeOverride("font_size", 26);
            titleHeader.CustomMinimumSize = new Vector2(0, 0);
            vbox.AddChild(titleHeader);
            vbox.AddChild(StyleHelper.CreateDivider(ModTheme.Divider, height: 1f));
        }

        for (int s = 0; s < _sections.Count; s++)
        {
            var section = _sections[s];

            if (s > 0)
                vbox.AddChild(StyleHelper.CreateDivider(ModTheme.Divider, height: 2f));

            // Sub-header — monster name + portrait
            var headerBox = new HBoxContainer();
            headerBox.AddThemeConstantOverride("separation", 16);
            int fontSize = multiMonster ? 22 : 26;
            var header = StyleHelper.CreateSectionHeader(section.Name, ModTheme.SectionHeader, fontSize: fontSize, outlineSize: 5);
            header.AutoSizeEnabled = false;
            header.AddThemeFontSizeOverride("font_size", fontSize);
            header.CustomMinimumSize = new Vector2(0, 0);
            header.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            headerBox.AddChild(header);
            AddBossOrCreaturePortrait(headerBox, section);
            vbox.AddChild(headerBox);

            // Column headers row
            vbox.AddChild(BuildColumnHeaders(nameColWidth, effectColWidth));

            // Move rows
            for (int i = 0; i < section.Patterns.Count; i++)
            {
                vbox.AddChild(BuildMoveRow(section.Patterns[i], i % 2 == 1, nameColWidth, effectColWidth));
            }
        }
    }

    private float MeasureNameColumnWidth()
    {
        if (_kreonFont == null) return 100f;
        float max = 0f;
        foreach (var section in _sections)
            foreach (var pattern in section.Patterns)
            {
                float w = _kreonFont.GetStringSize(pattern.Name, fontSize: FontSizeLabel).X;
                if (w > max) max = w;
            }
        return max + ColumnPadding;
    }

    private static readonly Regex BbCodeImgRegex = new(@"\[img[^\]]*\][^\[]*\[/img\]", RegexOptions.Compiled);
    private static readonly Regex BbCodeTagRegex = new(@"\[.*?\]", RegexOptions.Compiled);

    private float MeasureEffectColumnWidth()
    {
        if (_kreonFont == null) return 200f;
        float max = 0f;
        foreach (var section in _sections)
            foreach (var pattern in section.Patterns)
            {
                // Count inline icons before stripping
                int iconCount = BbCodeImgRegex.Matches(pattern.EffectBBCode).Count;
                // Strip [img]..path..[/img] entirely, then remaining tags
                string plain = BbCodeImgRegex.Replace(pattern.EffectBBCode, "");
                plain = BbCodeTagRegex.Replace(plain, "");
                float w = _kreonFont.GetStringSize(plain, fontSize: FontSizeEffect).X;
                w += iconCount * InlineIconWidth;
                if (w > max) max = w;
            }
        return max + ColumnPadding;
    }

    private void PositionCard(PanelContainer panel, float cardWidth)
    {
        panel.Position = new Vector2(520f, 100f);
        panel.CustomMinimumSize = new Vector2(cardWidth, 0);
    }

    private void AddBossOrCreaturePortrait(HBoxContainer container, MonsterSection section)
    {
        if (string.IsNullOrEmpty(section.MonsterEntry)) return;

        try
        {
            // For bosses, use the run_history encounter sprite
            if (!string.IsNullOrEmpty(section.EncounterSlug))
            {
                string bossPath = $"res://images/ui/run_history/{section.EncounterSlug}.png";
                var bossTex = ResourceLoader.Load<Texture2D>(bossPath);
                if (bossTex != null)
                {
                    var texRect = new TextureRect
                    {
                        Texture = bossTex,
                        CustomMinimumSize = new Vector2(PortraitSize, PortraitSize),
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                        MouseFilter = MouseFilterEnum.Ignore,
                    };
                    container.AddChild(texRect);
                    return;
                }
            }

            // Fallback: render creature visuals via SubViewport
            string entry = section.MonsterEntry.ToLowerInvariant();
            string visualsPath = $"res://scenes/creature_visuals/{entry}.tscn";
            var scene = ResourceLoader.Load<PackedScene>(visualsPath);
            if (scene == null) return;

            var viewport = new SubViewport
            {
                Size = new Vector2I(PortraitSize * 2, PortraitSize * 2),
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
            };

            var visuals = scene.Instantiate<Node2D>();
            visuals.Position = new Vector2(PortraitSize, PortraitSize * 1.7f);
            visuals.Scale = new Vector2(0.4f, 0.4f);
            viewport.AddChild(visuals);
            AddChild(viewport);

            var texRect2 = new TextureRect
            {
                Texture = viewport.GetTexture(),
                CustomMinimumSize = new Vector2(PortraitSize, PortraitSize),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            container.AddChild(texRect2);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"IntentPatterns portrait: {e.Message}");
        }
    }

    private Control BuildColumnHeaders(float nameColWidth, float effectColWidth)
    {
        // Match the row padding so columns align
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 5);
        margin.AddThemeConstantOverride("margin_right", 5);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 12);

        hbox.AddChild(MakeHeaderLabel("Name", nameColWidth));
        hbox.AddChild(MakeHeaderLabel("Intent", IntentColWidth, centered: true));
        hbox.AddChild(MakeHeaderLabel("Effect", effectColWidth));

        margin.AddChild(hbox);
        return margin;
    }

    private Label MakeHeaderLabel(string text, float minWidth, bool centered = false)
    {
        var lbl = new Label
        {
            Text = text,
            HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        if (_kreonFont != null) lbl.AddThemeFontOverride("font", _kreonFont);
        lbl.AddThemeFontSizeOverride("font_size", FontSizeHeader);
        lbl.AddThemeColorOverride("font_color", ModTheme.TextDim);
        if (minWidth > 0)
            lbl.CustomMinimumSize = new Vector2(minWidth, 0);
        return lbl;
    }

    private Control BuildMoveRow(ResolvedPattern pattern, bool alternate, float nameColWidth, float effectColWidth)
    {
        var bgColor = alternate
            ? new Color(ModTheme.PanelBg.R, ModTheme.PanelBg.G, ModTheme.PanelBg.B, 0.3f)
            : new Color(0, 0, 0, 0f);
        var panel = StyleHelper.CreateDarkPanel(bgColor, cornerRadius: 6, marginH: 5f, marginV: 4f);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 12);
        hbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        panel.AddChild(hbox);

        // Name column
        var nameLabel = new Label
        {
            Text = pattern.Name,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(nameColWidth, 0),
        };
        if (_kreonFont != null) nameLabel.AddThemeFontOverride("font", _kreonFont);
        nameLabel.AddThemeFontSizeOverride("font_size", FontSizeLabel);
        nameLabel.AddThemeColorOverride("font_color", ModTheme.TextAccent);
        hbox.AddChild(nameLabel);

        // Intent icons column — CenterContainer ensures vertical centering
        var intentCenter = new CenterContainer
        {
            CustomMinimumSize = new Vector2(IntentColWidth, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        var intentBox = new HBoxContainer();
        intentBox.AddThemeConstantOverride("separation", 4);

        foreach (var icon in pattern.Intents)
        {
            var texRect = new TextureRect
            {
                Texture = icon.Texture,
                CustomMinimumSize = new Vector2(IntentIconSize, IntentIconSize),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
                TooltipText = icon.Label,
            };
            intentBox.AddChild(texRect);
        }
        intentCenter.AddChild(intentBox);
        hbox.AddChild(intentCenter);

        // Effect column (BBCode rich text)
        var effectLabel = new RichTextLabel
        {
            BbcodeEnabled = true,
            Text = pattern.EffectBBCode,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(effectColWidth, 0),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.Off,
        };
        if (_kreonFont != null) effectLabel.AddThemeFontOverride("normal_font", _kreonFont);
        effectLabel.AddThemeFontSizeOverride("normal_font_size", FontSizeEffect);
        effectLabel.AddThemeColorOverride("default_color", ModTheme.TextLabel);
        hbox.AddChild(effectLabel);

        return panel;
    }
}
