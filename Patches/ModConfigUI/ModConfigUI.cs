using System;
using dubiousQOL.Config;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Runs;

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
        if (rowLabel != null) rowLabel.CallDeferred("set_text", "dubiousQOL Mod Configuration");
        var btnLabel = button.GetNodeOrNull<Label>("Label");
        if (btnLabel != null) btnLabel.CallDeferred("set_text", "Open Config");

        button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => DubiousConfigModal.Open(screen)));

        if (RunManager.Instance.IsInProgress)
            button.CallDeferred(NClickableControl.MethodName.Disable);
    }
}

internal partial class DubiousConfigPanel : Control, IScreenContext
{
    private Control? _firstFocusable;
    public Control? DefaultFocusedControl => _firstFocusable;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape)
        {
            var popup = FindChild("RestoreConfirmPopup", recursive: true, owned: false);
            if (popup != null)
                popup.QueueFree();
            else
                NModalContainer.Instance?.Clear();
            GetViewport().SetInputAsHandled();
        }
    }
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
        frame.AnchorLeft = 0.1f; frame.AnchorRight = 0.9f;
        frame.AnchorTop = 0.08f; frame.AnchorBottom = 0.92f;
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

        // Tab rows: 5 tabs per row so names aren't truncated
        var tabContainer = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        tabContainer.AddThemeConstantOverride("separation", 2);
        outerVbox.AddChild(tabContainer);

        var contentArea = new PanelContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        var contentStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.10f, 0.15f, 1.0f),
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

        // Build tab buttons in rows of 5
        const int tabsPerRow = 5;
        var allTabNames = new string[features.Count + 1];
        allTabNames[0] = "Home";
        for (int i = 0; i < features.Count; i++)
            allTabNames[i + 1] = features[i].Name;

        var tabButtons = new Button[allTabNames.Length];
        HBoxContainer? currentRow = null;
        for (int i = 0; i < allTabNames.Length; i++)
        {
            if (i % tabsPerRow == 0)
            {
                currentRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                currentRow.AddThemeConstantOverride("separation", 2);
                tabContainer.AddChild(currentRow);
            }
            tabButtons[i] = MakeTabButton(allTabNames[i]);
            currentRow!.AddChild(tabButtons[i]);
        }

        for (int i = 0; i < tabButtons.Length; i++)
        {
            int idx = i;
            tabButtons[i].Pressed += () =>
            {
                if (activeTab == idx) return;
                contentArea.RemoveChild(pages[activeTab]);
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

        var pad = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        pad.AddThemeConstantOverride("margin_left", 12);
        pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddThemeConstantOverride("margin_top", 12);
        pad.AddThemeConstantOverride("margin_bottom", 12);
        pad.AddChild(vbox);
        scroll.AddChild(pad);

        var hint = new Label
        {
            Text = "Toggle features on or off. Click a feature tab for detailed settings.\n\u26A0toggling requires a restart.",
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

            var nameLabel = new Label { Text = config.Name + (config.RequiresRestart ? "  \u26A0" : "") };
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

        var pad = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
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

        // Collect entry controls so Restore to Defaults can update them.
        var entryControls = new System.Collections.Generic.List<(ConfigEntry entry, Control control)>();

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
            {
                var row = BuildEntryRow(config, entry);
                vbox.AddChild(row);
                entryControls.Add((entry, row));
            }
        }

        var restoreBtn = new Button
        {
            Text = "Restore to Defaults",
            CustomMinimumSize = new Vector2(180, 32),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        restoreBtn.AddThemeFontSizeOverride("font_size", 13);
        if (config.Entries.Count == 0)
        {
            // TODO: enable when config settings are added for this feature
            restoreBtn.Disabled = true;
        }
        else
        {
            var capturedControls = entryControls;
            var capturedConfig = config;
            restoreBtn.Pressed += () => ShowRestoreConfirmation(capturedConfig, capturedControls);
        }
        vbox.AddChild(restoreBtn);

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
                // Min/Max must be set before Value — Godot's Range clamps on assignment.
                var spin = new SpinBox
                {
                    MinValue = entry.Min ?? 0,
                    MaxValue = entry.Max ?? 9999,
                    Value = (int)entry.Value,
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
                    MinValue = entry.Min ?? 0f,
                    MaxValue = entry.Max ?? 9999f,
                    Value = (float)entry.Value,
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

    private static void ShowRestoreConfirmation(
        FeatureConfig config,
        System.Collections.Generic.List<(ConfigEntry entry, Control control)> entryControls)
    {
        var popup = new Control { Name = "RestoreConfirmPopup", MouseFilter = Control.MouseFilterEnum.Stop };
        popup.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        // Dim backdrop
        var backdrop = new ColorRect { Color = new Color(0f, 0f, 0f, 0.5f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        popup.AddChild(backdrop);

        var panel = new PanelContainer();
        panel.AnchorLeft = 0.375f; panel.AnchorRight = 0.625f;
        panel.AnchorTop = 0.425f; panel.AnchorBottom = 0.575f;
        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.12f, 0.18f, 1f),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            BorderColor = AccentColor,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
        };
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        popup.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 16);
        vbox.Alignment = BoxContainer.AlignmentMode.Center;
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        margin.AddChild(vbox);
        panel.AddChild(margin);

        var msg = new Label
        {
            Text = $"Restore all {config.Name} settings to their default values?",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        msg.AddThemeFontSizeOverride("font_size", 15);
        vbox.AddChild(msg);

        var btnRow = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        btnRow.AddThemeConstantOverride("separation", 16);

        var cancelBtn = new Button
        {
            Text = "Cancel",
            CustomMinimumSize = new Vector2(120, 34),
        };
        cancelBtn.AddThemeFontSizeOverride("font_size", 14);
        cancelBtn.Pressed += () => popup.QueueFree();
        btnRow.AddChild(cancelBtn);

        var confirmBtn = new Button
        {
            Text = "Restore",
            CustomMinimumSize = new Vector2(120, 34),
        };
        confirmBtn.AddThemeFontSizeOverride("font_size", 14);
        confirmBtn.Pressed += () =>
        {
            foreach (var entry in config.Entries)
                entry.ResetToDefault();
            config.Save();
            foreach (var (entry, row) in entryControls)
                UpdateEntryControl(entry, row);
            popup.QueueFree();
        };
        btnRow.AddChild(confirmBtn);

        vbox.AddChild(btnRow);

        // Add to the config modal's root so it overlays the content.
        NModalContainer.Instance?.GetChild(NModalContainer.Instance.GetChildCount() - 1)?.AddChild(popup);
    }

    private static void UpdateEntryControl(ConfigEntry entry, Control row)
    {
        if (row is not HBoxContainer hbox) return;
        // The input control is the last child of the row HBoxContainer.
        var last = hbox.GetChild(hbox.GetChildCount() - 1);
        switch (entry.Type)
        {
            case ConfigEntryType.Bool when last is CheckBox cb:
                cb.SetPressedNoSignal((bool)entry.Value);
                break;
            case ConfigEntryType.Int when last is SpinBox spinI:
                spinI.Value = (int)entry.Value;
                break;
            case ConfigEntryType.Float when last is SpinBox spinF:
                spinF.Value = (float)entry.Value;
                break;
            case ConfigEntryType.Color when last is ColorPickerButton picker:
                picker.Color = (Color)entry.Value;
                break;
            case ConfigEntryType.Enum when last is OptionButton opt:
                if (entry.EnumOptions != null)
                    for (int i = 0; i < entry.EnumOptions.Length; i++)
                        if (entry.EnumOptions[i] == (string)entry.Value) { opt.Selected = i; break; }
                break;
        }
    }

    private static Button MakeTabButton(string text)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 28),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.None,
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
