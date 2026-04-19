using System;
using System.Collections.Generic;
using dubiousQOL.Config;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Runs;

using dubiousQOL.UI;
using ModTheme = dubiousQOL.UI.Theme;

namespace dubiousQOL.Patches;

// ─────────────────────────────────────────────────────────────
//  Inject "Open Config" row into the game's General settings
// ─────────────────────────────────────────────────────────────

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

        var newDivider = CloneHelper.Clone<Node>(divider, CloneHelper.Full)!;
        var newRow = CloneHelper.Clone<MarginContainer>(modding, CloneHelper.Full)!;
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

// ─────────────────────────────────────────────────────────────
//  Root panel — handles Escape key for popup/modal dismissal
// ─────────────────────────────────────────────────────────────

internal partial class DubiousConfigPanel : Control, IScreenContext
{
    private Control? _firstFocusable;
    public Control? DefaultFocusedControl => _firstFocusable;

    // _Input fires before NBackButton's hotkey handler in _UnhandledInput.
    // Without this, pressing Escape while a confirmation popup is open
    // would dismiss both the popup AND the modal in the same frame.
    public override void _Input(InputEvent @event)
    {
        ModalHelper.TryDismissChildPopup(@event, this, "RestoreConfirmPopup");
    }
}

// ─────────────────────────────────────────────────────────────
//  Modal builder — clones game settings screen components
// ─────────────────────────────────────────────────────────────

internal static class DubiousConfigModal
{
    // Shortened names for tab buttons so text fits within the tab texture.
    private static string ShortenTabName(string fullName) => fullName switch
    {
        "Act Name Display" => "Act Name",
        "Incoming Damage Display" => "DMG Display",
        "Rarity Display" => "Rarity Display",
        "Skip Splash Screen" => "Skip Splash",
        "Unified Save Path" => "Save Path",
        "Win Streak Display" => "Win Streak",
        _ => fullName,
    };

    public static void Open(NSettingsScreen settingsScreen)
    {
        try
        {
            var modal = NModalContainer.Instance;
            if (modal == null) return;

            // Hide the settings screen so it doesn't show through
            settingsScreen.Visible = false;

            var panel = BuildPanel();

            // Restore settings screen visibility when our panel leaves the tree
            var screenRef = settingsScreen;
            panel.TreeExiting += () =>
            {
                if (GodotObject.IsInstanceValid(screenRef))
                    screenRef.Visible = true;
            };

            modal.Add(panel, showBackstop: false);
        }
        catch (Exception e)
        {
            settingsScreen.Visible = true;
            MainFile.Logger.Warn($"ModConfigUI open: {e.Message}\n{e.StackTrace}");
        }
    }

    private static Control BuildPanel()
    {
        var root = new DubiousConfigPanel
        {
            Name = "DubiousConfigPanelRoot",
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var features = ConfigRegistry.All;

        // ── Tab bar ──────────────────────────────────────────
        var tabNames = new string[features.Count + 1];
        tabNames[0] = "Main";
        for (int i = 0; i < features.Count; i++)
            tabNames[i + 1] = ShortenTabName(features[i].Name);

        var (tabBarContainer, allTabs) = TabHelper.CreateTabBar(tabNames, tabsPerRow: 5);
        tabBarContainer.Name = "TabBarContainer";
        tabBarContainer.AnchorLeft = 0f; tabBarContainer.AnchorRight = 1f;
        tabBarContainer.AnchorTop = 0f; tabBarContainer.AnchorBottom = 0f;
        tabBarContainer.OffsetLeft = 200; tabBarContainer.OffsetRight = -200;
        tabBarContainer.OffsetTop = 60; tabBarContainer.OffsetBottom = 256;
        root.AddChild(tabBarContainer);

        // ── Content area ────────────────────────────────────
        var scroll = new ScrollContainer
        {
            Name = "ConfigScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        scroll.AnchorLeft = 0f; scroll.AnchorRight = 1f;
        scroll.AnchorTop = 0f; scroll.AnchorBottom = 1f;
        scroll.OffsetLeft = 340; scroll.OffsetRight = -400;
        scroll.OffsetTop = 260; scroll.OffsetBottom = -40;
        // Clicking anywhere in the scroll area releases focused LineEdits.
        scroll.GuiInput += inputEvent =>
        {
            if (inputEvent is InputEventMouseButton { Pressed: true })
                scroll.GetViewport()?.GuiReleaseFocus();
        };
        root.AddChild(scroll);

        // Build all pages
        var pages = new Control[features.Count + 1];
        pages[0] = BuildHomePage();
        for (int i = 0; i < features.Count; i++)
            pages[i + 1] = BuildFeaturePage(features[i]);

        scroll.AddChild(pages[0]);
        TabHelper.WireTabSwitching(allTabs, pages, scroll);

        // ── Back button ─────────────────────────────────────
        ModalHelper.CreateBackButton(root, "DubiousConfigBackButton");

        return root;
    }

    // ─────────────────────────────────────────────────────────
    //  Home page — feature toggle list
    // ─────────────────────────────────────────────────────────

    private static Control BuildHomePage()
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 0);
        vbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var hint = WidgetHelper.CreateInfoLabel(
            "Toggle features on or off. Click a feature tab for detailed settings.\n" +
            "Features marked with \u26A0 require a game restart to take effect.", 22);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.CustomMinimumSize = new Vector2(0, 72);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.AddThemeColorOverride("font_color", ModTheme.TextDim);
        vbox.AddChild(hint);

        vbox.AddChild(StyleHelper.CreateDivider(ModTheme.Divider));

        foreach (var config in ConfigRegistry.All)
        {
            var row = WidgetHelper.CreateSettingsRow(
                config.Name + (config.RequiresRestart ? "  \u26A0" : ""));

            row.AddChild(WidgetHelper.CreateGameTickbox(config.Enabled, ticked =>
            {
                config.Enabled = ticked;
                config.Save();
            }));

            vbox.AddChild(row);
            vbox.AddChild(StyleHelper.CreateDivider(ModTheme.Divider));
        }

        return vbox;
    }

    // ─────────────────────────────────────────────────────────
    //  Feature detail page
    // ─────────────────────────────────────────────────────────

    private static Control BuildFeaturePage(FeatureConfig config)
    {
        // Wrap in a MarginContainer so content stays inset from scroll edges
        var margin = new MarginContainer();
        margin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        margin.AddThemeConstantOverride("margin_right", 0);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 0);
        vbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        margin.AddChild(vbox);

        var desc = WidgetHelper.CreateInfoLabel(config.Description, 24);
        desc.HorizontalAlignment = HorizontalAlignment.Center;
        desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        desc.AddThemeColorOverride("font_color", ModTheme.TextDim);
        desc.CustomMinimumSize = new Vector2(0, 56);
        desc.VerticalAlignment = VerticalAlignment.Center;
        vbox.AddChild(desc);

        vbox.AddChild(StyleHelper.CreateDivider(ModTheme.Divider));

        var entryControls = new List<(ConfigEntry entry, Control control)>();

        if (config.Entries.Count == 0)
        {
            var empty = WidgetHelper.CreateInfoLabel("No additional settings for this feature.", 26);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.CustomMinimumSize = new Vector2(0, 80);
            empty.VerticalAlignment = VerticalAlignment.Center;
            empty.AddThemeColorOverride("font_color", ModTheme.TextDim);
            vbox.AddChild(empty);
        }
        else
        {
            foreach (var entry in config.Entries)
            {
                var row = BuildEntryRow(config, entry);
                vbox.AddChild(row);
                vbox.AddChild(StyleHelper.CreateDivider(ModTheme.Divider));
                entryControls.Add((entry, row));
            }
        }

        // Restore to Defaults — clone the game's hexagonal styled button
        var restoreRow = new MarginContainer();
        restoreRow.AddThemeConstantOverride("margin_left", 12);
        restoreRow.AddThemeConstantOverride("margin_right", 12);
        restoreRow.AddThemeConstantOverride("margin_top", 20);
        restoreRow.CustomMinimumSize = new Vector2(0, 80);

        var (restoreBtn, isGameBtn) = WidgetHelper.CreateGameButton("Restore Defaults");
        restoreBtn.Name = "RestoreDefaultsButton";

        if (config.Entries.Count == 0)
        {
            if (restoreBtn is Button plainBtn) plainBtn.Disabled = true;
            else restoreBtn.CallDeferred(NClickableControl.MethodName.Disable);
            restoreBtn.Modulate = new Color(1f, 1f, 1f, 0.35f);
        }
        else
        {
            var capturedControls = entryControls;
            var capturedConfig = config;
            if (isGameBtn)
            {
                restoreBtn.CallDeferred(NClickableControl.MethodName.Enable);
                restoreBtn.Connect(NClickableControl.SignalName.Released,
                    Callable.From<NButton>(_ => ShowRestoreConfirmation(capturedConfig, capturedControls)));
            }
            else
            {
                ((Button)restoreBtn).Pressed += () => ShowRestoreConfirmation(capturedConfig, capturedControls);
            }
        }

        restoreRow.AddChild(restoreBtn);
        vbox.AddChild(restoreRow);

        return margin;
    }

    // ─────────────────────────────────────────────────────────
    //  Entry row (per config entry)
    // ─────────────────────────────────────────────────────────

    private static Control BuildEntryRow(FeatureConfig config, ConfigEntry entry)
    {
        var row = WidgetHelper.CreateSettingsRow(entry.Label);

        switch (entry.Type)
        {
            case ConfigEntryType.Bool:
            {
                row.AddChild(WidgetHelper.CreateGameTickbox((bool)entry.Value, ticked =>
                {
                    entry.Value = ticked;
                    config.Save();
                }));
                break;
            }
            case ConfigEntryType.Int:
            case ConfigEntryType.Float:
            {
                row.AddChild(CreateGameSlider(config, entry));
                break;
            }
            case ConfigEntryType.Color:
            {
                var picker = new ColorPickerButton
                {
                    Color = (Color)entry.Value,
                    CustomMinimumSize = new Vector2(80, 48),
                    SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
                };
                picker.ColorChanged += c => { entry.Value = c; config.Save(); };
                row.AddChild(picker);
                break;
            }
            case ConfigEntryType.Enum:
            {
                var optionBtn = new OptionButton
                {
                    CustomMinimumSize = new Vector2(180, 48),
                    SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
                };
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

    private static Control CreateGameSlider(FeatureConfig config, ConfigEntry entry)
    {
        bool isInt = entry.Type == ConfigEntryType.Int;
        double min = entry.Min ?? 0;
        double max = entry.Max ?? 9999;
        double val = isInt ? (int)entry.Value : (float)entry.Value;

        return WidgetHelper.CreateGameSlider(val, min, max, isInt, realVal =>
        {
            if (isInt)
                entry.Value = (int)realVal;
            else
                entry.Value = (float)realVal;
            config.Save();
        });
    }

    private static void ShowRestoreConfirmation(
        FeatureConfig config,
        List<(ConfigEntry entry, Control control)> entryControls)
    {
        var modal = NModalContainer.Instance;
        var panelRoot = modal?.GetChild(modal.GetChildCount() - 1);
        if (panelRoot == null) return;

        ModalHelper.ShowConfirmation(
            (Control)(panelRoot as Control ?? modal!),
            "RestoreConfirmPopup",
            "Restore Defaults",
            $"Restore all {config.Name} settings\nto their default values?",
            "Restore", "Cancel",
            () => DoRestore(config, entryControls));
    }

    private static void DoRestore(FeatureConfig config, List<(ConfigEntry entry, Control control)> entryControls)
    {
        foreach (var entry in config.Entries)
            entry.ResetToDefault();
        config.Save();
        foreach (var (entry, row) in entryControls)
            UpdateEntryControl(entry, row);
    }

    private static void UpdateEntryControl(ConfigEntry entry, Control row)
    {
        if (row is not HBoxContainer hbox) return;
        var last = hbox.GetChild(hbox.GetChildCount() - 1);

        switch (entry.Type)
        {
            case ConfigEntryType.Bool:
                if (last is Control tickbox)
                    WidgetHelper.SetTickboxValue(tickbox, (bool)entry.Value);
                break;
            case ConfigEntryType.Int:
            case ConfigEntryType.Float:
                if (last is Control sliderContainer)
                    WidgetHelper.SetSliderValue(sliderContainer,
                        entry.Type == ConfigEntryType.Int ? (int)entry.Value : (float)entry.Value,
                        entry.Min ?? 0, entry.Type == ConfigEntryType.Int);
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
}
