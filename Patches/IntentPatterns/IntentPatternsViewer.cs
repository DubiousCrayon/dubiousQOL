using System;
using System.Collections.Generic;
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

    private readonly string _creatureName;
    private readonly string _monsterEntry;
    private readonly List<ResolvedPattern> _patterns;

    // Column widths
    private const float NameColWidth = 160f;
    private const float IntentColWidth = 90f;
    private const int IntentIconSize = 32;
    private const int FontSizeLabel = 20;
    private const int FontSizeEffect = 18;
    private const int FontSizeHeader = 16;
    private const float CardWidth = 560f;
    private const int PortraitSize = 40;

    public IntentPatternsViewer(string creatureName, List<ResolvedPattern> patterns, string monsterEntry)
    {
        _creatureName = creatureName;
        _monsterEntry = monsterEntry;
        _patterns = patterns;
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
        PositionCard(outerPanel);
        AddChild(outerPanel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        outerPanel.AddChild(vbox);

        // Section header — creature name + portrait in HBox
        var headerBox = new HBoxContainer();
        headerBox.AddThemeConstantOverride("separation", 16);
        var header = StyleHelper.CreateSectionHeader(_creatureName, ModTheme.SectionHeader, fontSize: 26, outlineSize: 5);
        header.AutoSizeEnabled = false;
        header.AddThemeFontSizeOverride("font_size", 26);
        header.CustomMinimumSize = new Vector2(0, 0);
        header.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        headerBox.AddChild(header);
        AddCreaturePortrait(headerBox);
        vbox.AddChild(headerBox);

        // Column headers row
        vbox.AddChild(BuildColumnHeaders());

        // Divider
        vbox.AddChild(StyleHelper.CreateDivider(ModTheme.Divider, height: 1f));

        // Move rows
        for (int i = 0; i < _patterns.Count; i++)
        {
            vbox.AddChild(BuildMoveRow(_patterns[i], i % 2 == 1));
        }
    }

    private void PositionCard(PanelContainer panel)
    {
        panel.Position = new Vector2(520f, 100f);
        panel.CustomMinimumSize = new Vector2(CardWidth, 0);
    }

    private void AddCreaturePortrait(HBoxContainer container)
    {
        if (string.IsNullOrEmpty(_monsterEntry)) return;

        try
        {
            string entry = _monsterEntry.ToLowerInvariant();
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

            var texRect = new TextureRect
            {
                Texture = viewport.GetTexture(),
                CustomMinimumSize = new Vector2(PortraitSize, PortraitSize),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            container.AddChild(texRect);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"IntentPatterns portrait: {e.Message}");
        }
    }

    private Control BuildColumnHeaders()
    {
        // Match the row padding so columns align
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 5);
        margin.AddThemeConstantOverride("margin_right", 5);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 12);

        hbox.AddChild(MakeHeaderLabel("Name", NameColWidth));
        hbox.AddChild(MakeHeaderLabel("Intent", IntentColWidth, centered: true));
        hbox.AddChild(MakeHeaderLabel("Effect", 0, expandFill: true));

        margin.AddChild(hbox);
        return margin;
    }

    private Label MakeHeaderLabel(string text, float minWidth, bool expandFill = false, bool centered = false)
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
        if (expandFill)
            lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return lbl;
    }

    private Control BuildMoveRow(ResolvedPattern pattern, bool alternate)
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
            CustomMinimumSize = new Vector2(NameColWidth, 0),
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
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        if (_kreonFont != null) effectLabel.AddThemeFontOverride("normal_font", _kreonFont);
        effectLabel.AddThemeFontSizeOverride("normal_font_size", FontSizeEffect);
        effectLabel.AddThemeColorOverride("default_color", ModTheme.TextLabel);
        hbox.AddChild(effectLabel);

        return panel;
    }
}
