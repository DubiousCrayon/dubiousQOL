using System;
using System.Collections.Generic;
using dubiousQOL.Config;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;

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

// ─────────────────────────────────────────────────────────────
//  Root panel — handles Escape key for popup/modal dismissal
// ─────────────────────────────────────────────────────────────

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

// ─────────────────────────────────────────────────────────────
//  Modal builder — clones game settings screen components
// ─────────────────────────────────────────────────────────────

internal static class DubiousConfigModal
{
    private static readonly Color DividerColor = new(0.909804f, 0.862745f, 0.745098f, 0.25098f);
    private static readonly Color DimTextColor = new(0.65f, 0.62f, 0.55f);
    private static readonly Color AccentColor = new(0.91f, 0.86f, 0.65f);

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

    // Source nodes from the live settings screen, cached per Open() call.
    // Typed as Node because Godot's C# interop doesn't resolve game script
    // types (NSettingsTab, NTickbox) when accessed via GetChildren/GetNode.
    private static Node? _sourceTab;
    private static Node? _sourceTickbox;
    private static Node? _sourceLabel;
    private static Node? _sourceButton;

    public static void Open(NSettingsScreen settingsScreen)
    {
        try
        {
            var modal = NModalContainer.Instance;
            if (modal == null) return;

            // Cache source nodes from the live settings screen for cloning
            _sourceTab = settingsScreen.GetNodeOrNull("SettingsTabManager/General");
            _sourceTickbox = settingsScreen.FindChild("SettingsTickbox", recursive: true, owned: false);
            _sourceLabel = settingsScreen.GetNodeOrNull(
                "ScrollContainer/Mask/Clipper/GeneralSettings/VBoxContainer/FastMode/Label");

            if (_sourceTab == null) MainFile.Logger.Warn("ModConfigUI: source tab not found");
            if (_sourceTickbox == null) MainFile.Logger.Warn("ModConfigUI: source tickbox not found");
            if (_sourceLabel == null) MainFile.Logger.Warn("ModConfigUI: source label not found");

            _sourceButton = settingsScreen.GetNodeOrNull(
                "ScrollContainer/Mask/Clipper/GeneralSettings/VBoxContainer/ResetGameplay/ResetGameplayButton");
            if (_sourceButton == null) MainFile.Logger.Warn("ModConfigUI: source button not found");

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
        finally
        {
            _sourceTab = null;
            _sourceTickbox = null;
            _sourceLabel = null;
            _sourceButton = null;
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

        var tabBarContainer = new VBoxContainer { Name = "TabBarContainer" };
        tabBarContainer.AddThemeConstantOverride("separation", 8);
        tabBarContainer.AnchorLeft = 0f; tabBarContainer.AnchorRight = 1f;
        tabBarContainer.AnchorTop = 0f; tabBarContainer.AnchorBottom = 0f;
        tabBarContainer.OffsetLeft = 200; tabBarContainer.OffsetRight = -200;
        tabBarContainer.OffsetTop = 60; tabBarContainer.OffsetBottom = 256;
        root.AddChild(tabBarContainer);

        const int tabsPerRow = 5;
        var allTabs = new Node?[tabNames.Length];
        HBoxContainer? currentRow = null;

        for (int i = 0; i < tabNames.Length && _sourceTab != null; i++)
        {
            if (i % tabsPerRow == 0)
            {
                currentRow = new HBoxContainer();
                currentRow.AddThemeConstantOverride("separation", 12);
                currentRow.Alignment = BoxContainer.AlignmentMode.Center;
                tabBarContainer.AddChild(currentRow);
            }

            // Duplicate(15) preserves scripts+groups+signals structure.
            // ModConfig (BaseLib) uses the same approach.
            var tab = _sourceTab.Duplicate(15);
            // Each tab needs its own shader material for independent HSV hover
            var tabImg = tab.GetNodeOrNull<TextureRect>("TabImage");
            if (tabImg?.Material is ShaderMaterial sm)
                tabImg.Material = (Material)sm.Duplicate();

            tab.Name = $"Tab_{i}";
            currentRow!.AddChild(tab);
            tab.CallDeferred("SetLabel", tabNames[i]);
            allTabs[i] = tab;
        }

        // Set initial tab selection state. Can't use Select/Deselect via
        // CallDeferred — clones start with _isSelected=false so Deselect's
        // guard blocks it. Instead, wire the Ready signal (fires after _Ready
        // populates _outline/_label) and directly set visual state.
        for (int i = 0; i < allTabs.Length; i++)
        {
            if (allTabs[i] == null) continue;
            var t = allTabs[i]!;
            bool selected = i == 0;
            t.Ready += () =>
            {
                var outline = t.GetNodeOrNull<TextureRect>("Outline");
                if (outline != null) outline.Visible = selected;
                var lbl = t.GetNodeOrNull<Control>("Label");
                if (lbl != null) lbl.Modulate = selected
                    ? new Color("FFF6E2")        // StsColors.cream
                    : new Color("FFF6E280");     // StsColors.halfTransparentCream
                t.Set("_isSelected", selected);  // sync internal state for future Select/Deselect
            };
        }

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
        root.AddChild(scroll);

        // Build all pages
        var pages = new Control[features.Count + 1];
        pages[0] = BuildHomePage();
        for (int i = 0; i < features.Count; i++)
            pages[i + 1] = BuildFeaturePage(features[i]);

        scroll.AddChild(pages[0]);
        int activeTab = 0;

        // ── Tab switching ───────────────────────────────────
        for (int i = 0; i < allTabs.Length; i++)
        {
            if (allTabs[i] == null) continue;
            int idx = i;
            var capturedTabs = allTabs;
            allTabs[i].Connect("Released", Callable.From<Variant>(_ =>
            {
                if (activeTab == idx) return;
                scroll.RemoveChild(pages[activeTab]);
                scroll.AddChild(pages[idx]);
                scroll.ScrollVertical = 0;
                capturedTabs[activeTab]?.Call("Deselect");
                capturedTabs[idx]?.Call("Select");
                activeTab = idx;
            }));
        }

        // ── Back button ─────────────────────────────────────
        try
        {
            var backBtn = PreloadManager.Cache.GetScene(
                SceneHelper.GetScenePath("ui/back_button")
            ).Instantiate<NBackButton>(PackedScene.GenEditState.Disabled);
            backBtn.Name = "DubiousConfigBackButton";
            backBtn.Released += _ => NModalContainer.Instance?.Clear();
            root.AddChild(backBtn);
            backBtn.CallDeferred(NClickableControl.MethodName.Enable);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"ModConfigUI back button: {e.Message}");
        }

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

        var hint = CreateInfoLabel(
            "Toggle features on or off. Click a feature tab for detailed settings.\n" +
            "Features marked with \u26A0 require a game restart to take effect.", 22);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.CustomMinimumSize = new Vector2(0, 72);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.AddThemeColorOverride("font_color", DimTextColor);
        vbox.AddChild(hint);

        vbox.AddChild(CreateDivider());

        foreach (var config in ConfigRegistry.All)
        {
            var row = CreateSettingsRow(
                config.Name + (config.RequiresRestart ? "  \u26A0" : ""));

            row.AddChild(CreateGameTickbox(config.Enabled, ticked =>
            {
                config.Enabled = ticked;
                config.Save();
            }));

            vbox.AddChild(row);
            vbox.AddChild(CreateDivider());
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

        var desc = CreateInfoLabel(config.Description, 24);
        desc.HorizontalAlignment = HorizontalAlignment.Center;
        desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        desc.AddThemeColorOverride("font_color", DimTextColor);
        desc.CustomMinimumSize = new Vector2(0, 56);
        desc.VerticalAlignment = VerticalAlignment.Center;
        vbox.AddChild(desc);

        vbox.AddChild(CreateDivider());

        var entryControls = new List<(ConfigEntry entry, Control control)>();

        if (config.Entries.Count == 0)
        {
            var empty = CreateInfoLabel("No additional settings for this feature.", 26);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.CustomMinimumSize = new Vector2(0, 80);
            empty.VerticalAlignment = VerticalAlignment.Center;
            empty.AddThemeColorOverride("font_color", DimTextColor);
            vbox.AddChild(empty);
        }
        else
        {
            foreach (var entry in config.Entries)
            {
                var row = BuildEntryRow(config, entry);
                vbox.AddChild(row);
                vbox.AddChild(CreateDivider());
                entryControls.Add((entry, row));
            }
        }

        // Restore to Defaults — clone the game's hexagonal styled button
        var restoreRow = new MarginContainer();
        restoreRow.AddThemeConstantOverride("margin_left", 12);
        restoreRow.AddThemeConstantOverride("margin_right", 12);
        restoreRow.AddThemeConstantOverride("margin_top", 20);
        restoreRow.CustomMinimumSize = new Vector2(0, 80);

        Control restoreBtn;
        if (_sourceButton != null)
        {
            // Duplicate(14) = scripts + groups + instantiation, skip signals
            // to avoid inheriting the source button's Released handler.
            var clone = (Control)_sourceButton.Duplicate(14);
            clone.Name = "RestoreDefaultsButton";
            clone.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            // Set label text after _Ready populates MegaLabel internals.
            // Ready signal fires after _Ready, so SetTextAutoSize can run.
            // Use "Restore Defaults" (shorter) so text fits the hex texture.
            clone.Ready += () =>
            {
                var lbl = clone.FindChild("Label", recursive: true, owned: false);
                lbl?.Call("SetTextAutoSize", "Restore Defaults");
            };
            restoreBtn = clone;
        }
        else
        {
            restoreBtn = new Button
            {
                Text = "Restore to Defaults",
                CustomMinimumSize = new Vector2(240, 48),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            ApplyGameFont((Button)restoreBtn, 24);
        }

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
            if (_sourceButton != null)
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
        var row = CreateSettingsRow(entry.Label);

        switch (entry.Type)
        {
            case ConfigEntryType.Bool:
            {
                row.AddChild(CreateGameTickbox((bool)entry.Value, ticked =>
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

    // ─────────────────────────────────────────────────────────
    //  Game-styled UI factory helpers
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Clones a RichTextLabel from the live settings screen for row labels.
    /// This gives us the exact same MegaRichTextLabel with game fonts/theme.
    /// </summary>
    private static Control CloneSettingsLabel(string text)
    {
        if (_sourceLabel != null)
        {
            var clone = (Control)_sourceLabel.Duplicate(15);
            if (clone is RichTextLabel rtl)
                rtl.Text = text;
            clone.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            clone.CustomMinimumSize = new Vector2(0, 64);
            return clone;
        }
        // Fallback if source not available
        var label = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", 28);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.CustomMinimumSize = new Vector2(0, 64);
        return label;
    }

    /// <summary>
    /// MegaLabel for informational text (hints, descriptions) that doesn't
    /// need to match settings rows exactly.
    /// </summary>
    private static MegaLabel CreateInfoLabel(string text, int fontSize)
    {
        try
        {
            var regular = PreloadManager.Cache.GetAsset<Font>("res://themes/kreon_regular_shared.tres");
            var bold = PreloadManager.Cache.GetAsset<Font>("res://themes/kreon_bold_shared.tres");
            var theme = PreloadManager.Cache.GetAsset<Theme>("res://themes/settings_screen_line_header.tres");

            var label = new MegaLabel
            {
                Theme = theme,
                AutoSizeEnabled = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                FocusMode = Control.FocusModeEnum.None,
                Text = text,
            };
            label.AddThemeFontOverride("normal_font", regular);
            label.AddThemeFontOverride("bold_font", bold);
            label.AddThemeFontSizeOverride("font_size", fontSize);
            return label;
        }
        catch
        {
            var fallback = new MegaLabel
            {
                Text = text,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            fallback.AddThemeFontSizeOverride("font_size", fontSize);
            return fallback;
        }
    }

    private static void ApplyGameFont(Control control, int fontSize)
    {
        try
        {
            var regular = PreloadManager.Cache.GetAsset<Font>("res://themes/kreon_regular_shared.tres");
            control.AddThemeFontOverride("font", regular);
        }
        catch { /* font unavailable, use default */ }
        control.AddThemeFontSizeOverride("font_size", fontSize);
    }

    private static ColorRect CreateDivider()
    {
        return new ColorRect
        {
            CustomMinimumSize = new Vector2(0, 2),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Color = DividerColor,
        };
    }

    private static HBoxContainer CreateSettingsRow(string labelText)
    {
        var hbox = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 64),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        hbox.AddThemeConstantOverride("separation", 12);
        hbox.AddChild(CloneSettingsLabel(labelText));
        return hbox;
    }

    /// <summary>
    /// Clones a game-native NTickbox from the live settings screen.
    /// Falls back to a plain CheckBox if the source isn't available.
    /// </summary>
    private static Control CreateGameTickbox(bool initialValue, Action<bool> onChanged)
    {
        if (_sourceTickbox != null)
        {
            var clone = (Control)_sourceTickbox.Duplicate(15);
            clone.CustomMinimumSize = new Vector2(320, 64);
            clone.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;

            // IsTicked setter NREs before _Ready (children not resolved).
            // Defer the set so it runs after the clone enters the scene tree.
            if (!initialValue)
                clone.CallDeferred("set", "IsTicked", false);

            // Toggled signal fires after OnRelease toggles IsTicked and plays SFX.
            clone.Connect("Toggled", Callable.From<Variant>(_ =>
            {
                bool ticked = (bool)clone.Get("IsTicked");
                onChanged(ticked);
            }));

            return clone;
        }

        // Fallback: plain Godot CheckBox
        var cb = new CheckBox
        {
            ButtonPressed = initialValue,
            CustomMinimumSize = new Vector2(64, 64),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
        };
        cb.Toggled += v => onChanged(v);
        return cb;
    }

    /// <summary>
    /// Creates a slider + editable value input styled to match the game's
    /// sound-settings volume sliders.
    /// </summary>
    private static Control CreateGameSlider(FeatureConfig config, ConfigEntry entry)
    {
        bool isInt = entry.Type == ConfigEntryType.Int;
        double min = entry.Min ?? 0;
        double max = entry.Max ?? 9999;
        double val = isInt ? (int)entry.Value : (float)entry.Value;

        var container = new HBoxContainer { Name = "SliderContainer" };
        container.AddThemeConstantOverride("separation", 8);
        container.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        container.SizeFlagsStretchRatio = 0.5f;
        container.CustomMinimumSize = new Vector2(0, 64);

        var slider = new HSlider
        {
            Name = "ConfigSlider",
            MinValue = min,
            MaxValue = max,
            Value = val,
            Step = isInt ? 1 : 1,
            CustomMinimumSize = new Vector2(180, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        container.AddChild(slider);

        var input = new LineEdit
        {
            Name = "ConfigValue",
            Text = isInt ? ((int)val).ToString() : ((float)val).ToString("F0"),
            CustomMinimumSize = new Vector2(64, 40),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            Alignment = HorizontalAlignment.Center,
        };
        ApplyGameFont(input, 24);
        container.AddChild(input);

        // Right padding so the input doesn't sit flush against the divider edge
        var spacer = new Control { CustomMinimumSize = new Vector2(24, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        container.AddChild(spacer);

        bool updatingFromInput = false;

        slider.ValueChanged += v =>
        {
            if (isInt)
                entry.Value = (int)v;
            else
                entry.Value = (float)v;
            if (!updatingFromInput)
                input.Text = isInt ? ((int)v).ToString() : ((float)v).ToString("F0");
            config.Save();
        };

        input.TextChanged += text =>
        {
            if (double.TryParse(text, out var v))
            {
                v = Mathf.Clamp(v, min, max);
                updatingFromInput = true;
                slider.Value = v;
                updatingFromInput = false;
            }
        };

        // Correct the displayed text to the clamped value on commit
        void ClampInputText()
        {
            if (double.TryParse(input.Text, out var v))
            {
                v = Mathf.Clamp(v, min, max);
                input.Text = isInt ? ((int)v).ToString() : ((float)v).ToString("F0");
            }
            else
            {
                input.Text = isInt ? ((int)entry.Value).ToString() : ((float)entry.Value).ToString("F0");
            }
        }

        input.TextSubmitted += _ => { ClampInputText(); input.ReleaseFocus(); };
        input.FocusExited += ClampInputText;

        return container;
    }

    // ─────────────────────────────────────────────────────────
    //  Restore to Defaults confirmation — uses the game's
    //  NGenericPopup for native look and feel
    // ─────────────────────────────────────────────────────────

    private static void ShowRestoreConfirmation(
        FeatureConfig config,
        List<(ConfigEntry entry, Control control)> entryControls)
    {
        var modal = NModalContainer.Instance;
        var panelRoot = modal?.GetChild(modal.GetChildCount() - 1);
        if (panelRoot == null) return;

        var genericPopup = NGenericPopup.Create();
        if (genericPopup == null)
        {
            DoRestore(config, entryControls);
            return;
        }

        // Wrap in a named container so the Escape handler can find and dismiss it.
        // Includes a dark backdrop since the popup scene doesn't provide one
        // (NModalContainer normally supplies it via showBackstop).
        var wrapper = new Control
        {
            Name = "RestoreConfirmPopup",
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        wrapper.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var backdrop = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.5f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        wrapper.AddChild(backdrop);

        // Let the popup scene's own layout handle centering —
        // forcing FullRect overrides the scene's anchor/offset setup.
        wrapper.AddChild(genericPopup);
        // The popup was designed for a full-screen parent (NModalContainer),
        // so the wrapper being FullRect provides the right reference frame.
        panelRoot.AddChild(wrapper);

        // _Ready has fired: VerticalPopup children are initialized.
        var vp = genericPopup.GetNodeOrNull("VerticalPopup");
        if (vp == null) { wrapper.QueueFree(); DoRestore(config, entryControls); return; }

        vp.Call("SetText", "Restore Defaults",
            $"Restore all {config.Name} settings\nto their default values?");

        var yesBtn = vp.GetNodeOrNull("YesButton");
        var noBtn = vp.GetNodeOrNull("NoButton");
        if (yesBtn == null || noBtn == null) { wrapper.QueueFree(); DoRestore(config, entryControls); return; }

        yesBtn.Call("SetText", "Restore");
        noBtn.Call("SetText", "Cancel");
        yesBtn.Set("IsYes", true);
        ((Control)noBtn).Visible = true;

        yesBtn.CallDeferred(NClickableControl.MethodName.Enable);
        noBtn.CallDeferred(NClickableControl.MethodName.Enable);

        var capturedConfig = config;
        var capturedControls = entryControls;
        var capturedWrapper = wrapper;

        yesBtn.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
        {
            DoRestore(capturedConfig, capturedControls);
            capturedWrapper.QueueFree();
        }));

        noBtn.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
        {
            capturedWrapper.QueueFree();
        }));
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
                try { last.Set("IsTicked", (bool)entry.Value); }
                catch { if (last is CheckBox cb) cb.SetPressedNoSignal((bool)entry.Value); }
                break;
            case ConfigEntryType.Int:
            case ConfigEntryType.Float:
            {
                // Slider container: HBoxContainer { ConfigSlider (HSlider), ConfigValue (LineEdit) }
                if (last is not HBoxContainer sliderBox) break;
                var slider = sliderBox.GetNodeOrNull<Godot.Range>("ConfigSlider");
                var input = sliderBox.GetNodeOrNull<LineEdit>("ConfigValue");
                bool isInt = entry.Type == ConfigEntryType.Int;
                double v = isInt ? (int)entry.Value : (float)entry.Value;
                slider?.SetValueNoSignal(v);
                if (input != null)
                    input.Text = isInt ? ((int)v).ToString() : ((float)v).ToString("F0");
                break;
            }
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
