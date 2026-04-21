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
        float singleColWidth = nameColWidth + IntentColWidth + effectColWidth + CardMargins;
        float cardWidth;
        if (_sections.Count > 1)
        {
            // Sum per-section widths for side-by-side layout
            float total = 0;
            for (int i = 0; i < _sections.Count; i++)
            {
                var sectionList = new List<MonsterSection> { _sections[i] };
                float nw = MeasureNameColumnWidth(sectionList);
                float ew = MeasureEffectColumnWidth(sectionList);
                total += nw + IntentColWidth + ew + CardMargins;
            }
            total += (_sections.Count - 1) * 25f; // separator + padding
            cardWidth = total;
        }
        else
        {
            cardWidth = singleColWidth;
        }

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
            titleHeader.HorizontalAlignment = HorizontalAlignment.Center;
            titleHeader.CustomMinimumSize = new Vector2(0, 0);
            vbox.AddChild(titleHeader);
            vbox.AddChild(StyleHelper.CreateDivider(ModTheme.Divider, height: 2f));

            // Side-by-side columns for each monster
            var columnsBox = new HBoxContainer();
            columnsBox.AddThemeConstantOverride("separation", 0);
            vbox.AddChild(columnsBox);

            for (int s = 0; s < _sections.Count; s++)
            {
                if (s > 0)
                {
                    // Thin vertical separator with padding on all sides
                    var sepMargin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
                    sepMargin.AddThemeConstantOverride("margin_top", 8);
                    sepMargin.AddThemeConstantOverride("margin_bottom", 8);
                    sepMargin.AddThemeConstantOverride("margin_left", 12);
                    sepMargin.AddThemeConstantOverride("margin_right", 12);
                    var sep = new ColorRect
                    {
                        Color = ModTheme.Divider,
                        CustomMinimumSize = new Vector2(1, 0),
                        SizeFlagsVertical = SizeFlags.ExpandFill,
                        MouseFilter = MouseFilterEnum.Ignore,
                    };
                    sepMargin.AddChild(sep);
                    columnsBox.AddChild(sepMargin);
                }

                var section = _sections[s];
                var sectionList = new List<MonsterSection> { section };
                float secNameW = MeasureNameColumnWidth(sectionList);
                float secEffectW = MeasureEffectColumnWidth(sectionList);

                var colVbox = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
                colVbox.AddThemeConstantOverride("separation", 8);

                // Monster sub-header
                var headerBox = new HBoxContainer();
                headerBox.AddThemeConstantOverride("separation", 16);
                int fontSize = 22;
                var header = StyleHelper.CreateSectionHeader(section.Name, ModTheme.SectionHeader, fontSize: fontSize, outlineSize: 5);
                header.AutoSizeEnabled = false;
                header.AddThemeFontSizeOverride("font_size", fontSize);
                header.CustomMinimumSize = new Vector2(0, 0);
                header.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                headerBox.AddChild(header);
                AddBossOrCreaturePortrait(headerBox, section);
                AddHpLabel(headerBox, section, fontSize);
                colVbox.AddChild(headerBox);

                colVbox.AddChild(BuildColumnHeaders(secNameW, secEffectW));

                for (int i = 0; i < section.Patterns.Count; i++)
                    colVbox.AddChild(BuildMoveRow(section.Patterns[i], i % 2 == 1, secNameW, secEffectW));

                columnsBox.AddChild(colVbox);
                AddSpawnCountStamp(colVbox, section, fontSize);
            }
        }
        else
        {
            var section = _sections[0];

            var headerBox = new HBoxContainer();
            headerBox.AddThemeConstantOverride("separation", 16);
            int fontSize = 26;
            var header = StyleHelper.CreateSectionHeader(section.Name, ModTheme.SectionHeader, fontSize: fontSize, outlineSize: 5);
            header.AutoSizeEnabled = false;
            header.AddThemeFontSizeOverride("font_size", fontSize);
            header.CustomMinimumSize = new Vector2(0, 0);
            header.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            headerBox.AddChild(header);
            AddBossOrCreaturePortrait(headerBox, section);
            AddHpLabel(headerBox, section, fontSize);
            vbox.AddChild(headerBox);

            vbox.AddChild(BuildColumnHeaders(nameColWidth, effectColWidth));

            for (int i = 0; i < section.Patterns.Count; i++)
                vbox.AddChild(BuildMoveRow(section.Patterns[i], i % 2 == 1, nameColWidth, effectColWidth));
        }
    }

    private float MeasureNameColumnWidth() => MeasureNameColumnWidth(_sections);
    private float MeasureEffectColumnWidth() => MeasureEffectColumnWidth(_sections);

    private float MeasureNameColumnWidth(IEnumerable<MonsterSection> sections)
    {
        if (_kreonFont == null) return 100f;
        float max = 0f;
        foreach (var section in sections)
            foreach (var pattern in section.Patterns)
            {
                float w = _kreonFont.GetStringSize(pattern.Name, fontSize: FontSizeLabel).X;
                if (w > max) max = w;
            }
        return max + ColumnPadding;
    }

    private static readonly Regex BbCodeImgRegex = new(@"\[img[^\]]*\][^\[]*\[/img\]", RegexOptions.Compiled);
    private static readonly Regex BbCodeTagRegex = new(@"\[.*?\]", RegexOptions.Compiled);

    private float MeasureEffectColumnWidth(IEnumerable<MonsterSection> sections)
    {
        if (_kreonFont == null) return 200f;
        float max = 0f;
        foreach (var section in sections)
            foreach (var pattern in section.Patterns)
            {
                int iconCount = BbCodeImgRegex.Matches(pattern.EffectBBCode).Count;
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
        float x = _sections.Count > 1 ? Math.Max(50f, (1920f - cardWidth) / 2f) : 520f;
        panel.Position = new Vector2(x, 100f);
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

    private void AddHpLabel(HBoxContainer container, MonsterSection section, int fontSize)
    {
        if (!section.MinHp.HasValue) return;

        var chip = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        chip.AddThemeConstantOverride("separation", 6);
        chip.SizeFlagsVertical = SizeFlags.ShrinkCenter;

        var labelNode = new Label
        {
            Text = "HP",
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        if (_kreonFont != null) labelNode.AddThemeFontOverride("font", _kreonFont);
        labelNode.AddThemeFontSizeOverride("font_size", fontSize - 4);
        labelNode.AddThemeColorOverride("font_color", Colors.White);
        chip.AddChild(labelNode);

        string hpValue = section.MinHp == section.MaxHp
            ? $"{section.MinHp}"
            : $"{section.MinHp}-{section.MaxHp}";

        var valueNode = new Label
        {
            Text = hpValue,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        if (_kreonFont != null) valueNode.AddThemeFontOverride("font", _kreonFont);
        valueNode.AddThemeFontSizeOverride("font_size", fontSize);
        valueNode.AddThemeColorOverride("font_color", ModTheme.Damage);
        chip.AddChild(valueNode);

        container.AddChild(chip);
    }

    private static Font? _fightKidFont;

    private void AddSpawnCountStamp(Control container, MonsterSection section, int fontSize)
    {
        if (section.SpawnCount <= 1) return;

        if (_fightKidFont == null || !GodotObject.IsInstanceValid(_fightKidFont))
            _fightKidFont = FontHelper.Load("fightkid");

        var stamp = new Label
        {
            Text = $"x{section.SpawnCount}",
            MouseFilter = MouseFilterEnum.Ignore,
            TopLevel = true,
        };
        if (_fightKidFont != null) stamp.AddThemeFontOverride("font", _fightKidFont);
        stamp.AddThemeFontSizeOverride("font_size", fontSize + 6);
        stamp.AddThemeColorOverride("font_color", new Color(0.5f, 0.85f, 0.9f));

        container.AddChild(stamp);

        container.Ready += () =>
        {
            Callable.From(() =>
            {
                var globalRect = container.GetGlobalRect();
                stamp.PivotOffset = stamp.Size / 2f;
                stamp.RotationDegrees = -12f;
                stamp.GlobalPosition = new Vector2(
                    globalRect.End.X - stamp.Size.X - 8f,
                    globalRect.Position.Y - 4f
                );
            }).CallDeferred();
        };
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
