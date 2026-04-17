using System;
using dubiousQOL.Config;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace dubiousQOL.Patches;

[HarmonyPatch(typeof(NSettingsScreen), "_Ready")]
public static class PatchModConfigSettingsRow
{
    private const string RowName = "DubiousModConfigRow";

    [HarmonyPostfix]
    public static void Postfix(NSettingsScreen __instance)
    {
        try { InjectRow(__instance); }
        catch (Exception e) { MainFile.Logger.Warn($"ModConfigUI inject: {e.Message}"); }
    }

    private static void InjectRow(NSettingsScreen screen)
    {
        var general = screen.GetNodeOrNull<Control>("ScrollContainer/Mask/Clipper/GeneralSettings");
        var vbox = general?.GetNodeOrNull<Container>("VBoxContainer");
        if (vbox == null) return;
        if (vbox.HasNode(RowName)) return;

        var divider = vbox.GetNodeOrNull<ColorRect>("SendFeedbackDivider");
        var feedback = vbox.GetNodeOrNull<MarginContainer>("SendFeedback");
        var modding = vbox.GetNodeOrNull<MarginContainer>("Modding");
        if (divider == null || feedback == null || modding == null)
        {
            MainFile.Logger.Warn("ModConfigUI: expected settings rows missing, skipping injection.");
            return;
        }

        var newDivider = (Node)divider.Duplicate(15);
        var newRow = (MarginContainer)modding.Duplicate(15);
        newRow.UniqueNameInOwner = false;
        newRow.Name = RowName;

        var button = newRow.GetNode<Control>("ModdingButton");
        button.Name = "DubiousModConfigButton";
        button.UniqueNameInOwner = true;

        feedback.AddSibling(newDivider, false);
        newDivider.AddSibling(newRow, false);
        button.Owner = screen;

        var rowLabel = newRow.GetNodeOrNull<RichTextLabel>("Label");
        if (rowLabel != null) rowLabel.CallDeferred("set_text", "Mod Configuration (dubiousQOL)");
        var btnLabel = button.GetNodeOrNull<Label>("Label");
        if (btnLabel != null) btnLabel.CallDeferred("set_text", "Open Config");

        button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => DubiousConfigModal.Open(screen)));
    }
}

internal partial class DubiousConfigPanel : Control, IScreenContext
{
    private Control? _firstFocusable;
    public Control? DefaultFocusedControl => _firstFocusable;
}

internal static class DubiousConfigModal
{
    private static readonly Color AccentColor = new(0.9f, 0.85f, 0.6f);
    private static readonly Color DimText = new(0.75f, 0.75f, 0.75f);
    private static readonly Color TabActiveBg = new(0.22f, 0.22f, 0.30f, 0.9f);
    private static readonly Color TabInactiveBg = new(0.12f, 0.12f, 0.18f, 0.6f);

    public static void Open(NSettingsScreen settingsScreen)
    {
        try
        {
            var modal = NModalContainer.Instance;
            if (modal == null) return;
            modal.Add(BuildPanel());
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"ModConfigUI open: {e.Message}\n{e.StackTrace}");
        }
    }

    private static Control BuildPanel()
    {
        var root = new DubiousConfigPanel { Name = "DubiousConfigPanelRoot", MouseFilter = Control.MouseFilterEnum.Stop };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var frame = new PanelContainer { Name = "Frame" };
        frame.AnchorLeft = 0.5f; frame.AnchorRight = 0.5f;
        frame.AnchorTop = 0.5f; frame.AnchorBottom = 0.5f;
        frame.OffsetLeft = -320; frame.OffsetRight = 320;
        frame.OffsetTop = -280; frame.OffsetBottom = 280;
        root.AddChild(frame);

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 24);
        pad.AddThemeConstantOverride("margin_right", 24);
        pad.AddThemeConstantOverride("margin_top", 20);
        pad.AddThemeConstantOverride("margin_bottom", 20);
        frame.AddChild(pad);

        var outerVbox = new VBoxContainer();
        outerVbox.AddThemeConstantOverride("separation", 10);
        pad.AddChild(outerVbox);

        var title = new Label
        {
            Text = "dubiousQOL Configuration",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 22);
        outerVbox.AddChild(title);

        var tabBar = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        tabBar.AddThemeConstantOverride("separation", 2);
        outerVbox.AddChild(tabBar);

        var contentArea = new PanelContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        var contentStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.08f, 0.12f, 0.5f),
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        };
        contentArea.AddThemeStyleboxOverride("panel", contentStyle);
        outerVbox.AddChild(contentArea);

        var homePage = BuildHomePage();
        var features = ConfigRegistry.All;
        var pages = new Control[features.Count + 1];
        pages[0] = homePage;
        for (int i = 0; i < features.Count; i++)
            pages[i + 1] = BuildFeaturePage(features[i]);

        contentArea.AddChild(homePage);
        int activeTab = 0;

        var tabButtons = new Button[features.Count + 1];
        tabButtons[0] = MakeTabButton("Home");
        tabBar.AddChild(tabButtons[0]);
        for (int i = 0; i < features.Count; i++)
        {
            tabButtons[i + 1] = MakeTabButton(features[i].Name);
            tabBar.AddChild(tabButtons[i + 1]);
        }

        for (int i = 0; i < tabButtons.Length; i++)
        {
            int idx = i;
            tabButtons[i].Pressed += () =>
            {
                if (activeTab == idx) return;
                contentArea.GetChild(0)?.QueueFree();
                contentArea.AddChild(pages[idx]);
                activeTab = idx;
                UpdateTabHighlights(tabButtons, activeTab);
            };
        }
        UpdateTabHighlights(tabButtons, 0);

        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(140, 36) };
        close.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        close.Pressed += () => NModalContainer.Instance?.Clear();
        outerVbox.AddChild(close);

        return root;
    }

    private static Control BuildHomePage()
    {
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 12);
        pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddThemeConstantOverride("margin_top", 12);
        pad.AddThemeConstantOverride("margin_bottom", 12);
        pad.AddChild(vbox);
        scroll.AddChild(pad);

        var hint = new Label
        {
            Text = "Toggle features on or off. Click a feature tab for detailed settings.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 14);
        hint.AddThemeColorOverride("font_color", DimText);
        vbox.AddChild(hint);

        foreach (var config in ConfigRegistry.All)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);

            var nameVbox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

            var nameLabel = new Label { Text = config.Name + (config.RequiresRestart ? " (***)" : "") };
            nameLabel.AddThemeFontSizeOverride("font_size", 16);
            nameVbox.AddChild(nameLabel);

            var descLabel = new Label
            {
                Text = config.Description,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            descLabel.AddThemeFontSizeOverride("font_size", 12);
            descLabel.AddThemeColorOverride("font_color", DimText);
            nameVbox.AddChild(descLabel);

            row.AddChild(nameVbox);

            var cb = new CheckBox { ButtonPressed = config.Enabled };
            var capturedConfig = config;
            cb.Toggled += v => { capturedConfig.Enabled = v; capturedConfig.Save(); };
            row.AddChild(cb);

            vbox.AddChild(row);
        }

        return scroll;
    }

    private static Control BuildFeaturePage(FeatureConfig config)
    {
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 12);
        pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddThemeConstantOverride("margin_top", 12);
        pad.AddThemeConstantOverride("margin_bottom", 12);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);

        var header = new Label
        {
            Text = config.Name,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        header.AddThemeFontSizeOverride("font_size", 18);
        header.AddThemeColorOverride("font_color", AccentColor);
        vbox.AddChild(header);

        var desc = new Label
        {
            Text = config.Description,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        desc.AddThemeFontSizeOverride("font_size", 13);
        desc.AddThemeColorOverride("font_color", DimText);
        vbox.AddChild(desc);

        if (config.Entries.Count == 0)
        {
            var empty = new Label
            {
                Text = "No additional settings for this feature.",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            empty.AddThemeFontSizeOverride("font_size", 13);
            empty.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
            vbox.AddChild(empty);
        }
        else
        {
            foreach (var entry in config.Entries)
                vbox.AddChild(BuildEntryRow(config, entry));
        }

        pad.AddChild(vbox);
        scroll.AddChild(pad);
        return scroll;
    }

    private static Control BuildEntryRow(FeatureConfig config, ConfigEntry entry)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        var labelVbox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var label = new Label { Text = entry.Label };
        label.AddThemeFontSizeOverride("font_size", 14);
        labelVbox.AddChild(label);

        if (!string.IsNullOrEmpty(entry.Description))
        {
            var descLabel = new Label
            {
                Text = entry.Description,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            descLabel.AddThemeFontSizeOverride("font_size", 11);
            descLabel.AddThemeColorOverride("font_color", DimText);
            labelVbox.AddChild(descLabel);
        }
        row.AddChild(labelVbox);

        switch (entry.Type)
        {
            case ConfigEntryType.Bool:
            {
                var cb = new CheckBox { ButtonPressed = (bool)entry.Value };
                cb.Toggled += v => { entry.Value = v; config.Save(); };
                row.AddChild(cb);
                break;
            }
            case ConfigEntryType.Int:
            {
                var spin = new SpinBox
                {
                    Value = (int)entry.Value,
                    MinValue = entry.Min ?? 0,
                    MaxValue = entry.Max ?? 9999,
                    Step = 1,
                    CustomMinimumSize = new Vector2(100, 0),
                };
                spin.ValueChanged += v => { entry.Value = (int)v; config.Save(); };
                row.AddChild(spin);
                break;
            }
            case ConfigEntryType.Float:
            {
                var spin = new SpinBox
                {
                    Value = (float)entry.Value,
                    MinValue = entry.Min ?? 0f,
                    MaxValue = entry.Max ?? 9999f,
                    Step = 0.1,
                    CustomMinimumSize = new Vector2(100, 0),
                };
                spin.ValueChanged += v => { entry.Value = (float)v; config.Save(); };
                row.AddChild(spin);
                break;
            }
            case ConfigEntryType.Color:
            {
                var picker = new ColorPickerButton
                {
                    Color = (Color)entry.Value,
                    CustomMinimumSize = new Vector2(60, 28),
                };
                picker.ColorChanged += c => { entry.Value = c; config.Save(); };
                row.AddChild(picker);
                break;
            }
            case ConfigEntryType.Enum:
            {
                var optionBtn = new OptionButton { CustomMinimumSize = new Vector2(120, 0) };
                int selected = 0;
                if (entry.EnumOptions != null)
                {
                    for (int i = 0; i < entry.EnumOptions.Length; i++)
                    {
                        optionBtn.AddItem(entry.EnumOptions[i]);
                        if (entry.EnumOptions[i] == (string)entry.Value) selected = i;
                    }
                }
                optionBtn.Selected = selected;
                optionBtn.ItemSelected += idx =>
                {
                    entry.Value = entry.EnumOptions![(int)idx];
                    config.Save();
                };
                row.AddChild(optionBtn);
                break;
            }
        }

        return row;
    }

    private static Button MakeTabButton(string text)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 28),
            FocusMode = Control.FocusModeEnum.None,
            ClipText = true,
        };
        btn.AddThemeFontSizeOverride("font_size", 12);
        return btn;
    }

    private static void UpdateTabHighlights(Button[] tabs, int active)
    {
        for (int i = 0; i < tabs.Length; i++)
        {
            var bg = i == active ? TabActiveBg : TabInactiveBg;
            var style = new StyleBoxFlat { BgColor = bg };
            style.CornerRadiusTopLeft = 4;
            style.CornerRadiusTopRight = 4;
            if (i == active)
            {
                style.BorderColor = AccentColor;
                style.BorderWidthBottom = 2;
            }
            tabs[i].AddThemeStyleboxOverride("normal", style);
            tabs[i].AddThemeStyleboxOverride("hover", new StyleBoxFlat
            {
                BgColor = new Color(bg.R + 0.05f, bg.G + 0.05f, bg.B + 0.05f, bg.A),
                CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            });
            tabs[i].AddThemeStyleboxOverride("pressed", style);
            tabs[i].AddThemeColorOverride("font_color", i == active ? AccentColor : DimText);
            tabs[i].AddThemeColorOverride("font_hover_color", i == active ? AccentColor : new Color(0.9f, 0.9f, 0.9f));
        }
    }
}
