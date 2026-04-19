using System;
using System.Collections.Generic;
using dubiousQOL.Config;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;

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
    private static Node? _sourceTickbox;
    private static Node? _sourceLabel;
    private static Node? _sourceButton;
    private static Node? _sourceSlider;

    public static void Open(NSettingsScreen settingsScreen)
    {
        try
        {
            var modal = NModalContainer.Instance;
            if (modal == null) return;

            // Cache source nodes from the live settings screen for cloning
            _sourceTickbox = settingsScreen.FindChild("SettingsTickbox", recursive: true, owned: false);
            _sourceLabel = settingsScreen.GetNodeOrNull(
                "ScrollContainer/Mask/Clipper/GeneralSettings/VBoxContainer/FastMode/Label");

            if (_sourceTickbox == null) MainFile.Logger.Warn("ModConfigUI: source tickbox not found");
            if (_sourceLabel == null) MainFile.Logger.Warn("ModConfigUI: source label not found");

            _sourceButton = settingsScreen.GetNodeOrNull(
                "ScrollContainer/Mask/Clipper/GeneralSettings/VBoxContainer/ResetGameplay/ResetGameplayButton");
            if (_sourceButton == null) MainFile.Logger.Warn("ModConfigUI: source button not found");

            // Cache the NSlider from the sound settings for styled slider cloning
            var volumeSlider = settingsScreen.GetNodeOrNull(
                "ScrollContainer/Mask/Clipper/SoundSettings/VBoxContainer/MasterVolume/MasterVolumeSlider");
            _sourceSlider = volumeSlider?.GetNodeOrNull("Slider");
            if (_sourceSlider == null) MainFile.Logger.Warn("ModConfigUI: source slider not found");

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
            _sourceTickbox = null;
            _sourceLabel = null;
            _sourceButton = null;
            _sourceSlider = null;
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
        int activeTab = 0;

        // ── Tab switching ───────────────────────────────────
        for (int i = 0; i < allTabs.Count; i++)
        {
            int idx = i;
            var capturedTabs = allTabs;
            allTabs[i].Connect("Released", Callable.From<Variant>(_ =>
            {
                if (activeTab == idx) return;
                scroll.RemoveChild(pages[activeTab]);
                scroll.AddChild(pages[idx]);
                scroll.ScrollVertical = 0;
                capturedTabs[activeTab].Call("Deselect");
                capturedTabs[idx].Call("Select");
                activeTab = idx;
            }));
        }

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

        var hint = CreateInfoLabel(
            "Toggle features on or off. Click a feature tab for detailed settings.\n" +
            "Features marked with \u26A0 require a game restart to take effect.", 22);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.CustomMinimumSize = new Vector2(0, 72);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.AddThemeColorOverride("font_color", ModTheme.TextDim);
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
        desc.AddThemeColorOverride("font_color", ModTheme.TextDim);
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
            empty.AddThemeColorOverride("font_color", ModTheme.TextDim);
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
            var clone = CloneHelper.Clone<Control>(_sourceButton, CloneHelper.FullNoSignals)!;
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
            var clone = CloneHelper.Clone<Control>(_sourceLabel, CloneHelper.Full)!;
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
        var regular = FontHelper.Load("kreon-regular");
        var bold = FontHelper.Load("kreon-bold");
        var theme = FontHelper.LoadTheme("res://themes/settings_screen_line_header.tres");

        var label = new MegaLabel
        {
            Theme = theme,
            AutoSizeEnabled = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None,
            Text = text,
        };
        if (regular != null) label.AddThemeFontOverride("normal_font", regular);
        if (bold != null) label.AddThemeFontOverride("bold_font", bold);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        return label;
    }

    private static void ApplyGameFont(Control control, int fontSize)
    {
        var regular = FontHelper.Load("kreon-regular");
        if (regular != null) control.AddThemeFontOverride("font", regular);
        control.AddThemeFontSizeOverride("font_size", fontSize);
    }

    private static ColorRect CreateDivider() => StyleHelper.CreateDivider(ModTheme.Divider);

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
    /// Clones the visual shell of a game-native NTickbox (no scripts) and
    /// handles toggle logic manually. The source NCommonTooltipsTickbox has
    /// an OnUntick override that NullRefs outside the settings screen, so we
    /// strip scripts and drive the visuals ourselves.
    /// Falls back to a plain CheckBox if the source isn't available.
    /// </summary>
    private static Control CreateGameTickbox(bool initialValue, Action<bool> onChanged)
    {
        if (_sourceTickbox != null)
        {
            var clone = CloneHelper.Clone<Control>(_sourceTickbox, CloneHelper.VisualOnly)!;
            clone.CustomMinimumSize = new Vector2(320, 64);
            clone.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
            clone.MouseFilter = Control.MouseFilterEnum.Stop;

            var tickedImg = clone.FindChild("Ticked", recursive: true, owned: false) as Control;
            var notTickedImg = clone.FindChild("NotTicked", recursive: true, owned: false) as Control;
            var visuals = clone.FindChild("TickboxVisuals", recursive: true, owned: false) as Control;
            var baseScale = visuals?.Scale ?? Vector2.One;
            // The shader material is shared across all clones from the same
            // source — duplicate it so each tickbox has independent brightness.
            if (visuals?.Material is ShaderMaterial shared)
                visuals.Material = (ShaderMaterial)shared.Duplicate();
            var hsv = visuals?.Material as ShaderMaterial;

            bool ticked = initialValue;
            if (tickedImg != null) tickedImg.Visible = ticked;
            if (notTickedImg != null) notTickedImg.Visible = !ticked;

            // Emulate NTickbox hover/press/release visual effects.
            Tween? activeTween = null;
            void TweenVisuals(Vector2 targetScale, float targetV, double duration)
            {
                activeTween?.Kill();
                if (visuals == null) return;
                activeTween = clone.CreateTween().SetParallel();
                activeTween.TweenProperty(visuals, "scale", targetScale, duration);
                if (hsv != null)
                    activeTween.TweenMethod(
                        Callable.From<float>(v => hsv.SetShaderParameter("v", v)),
                        hsv.GetShaderParameter("v"), targetV, duration);
            }

            clone.MouseEntered += () =>
            {
                SfxCmd.Play("event:/sfx/ui/clicks/ui_hover");
                TweenVisuals(baseScale * 1.05f, 1.2f, 0.05);
            };
            clone.MouseExited += () =>
            {
                TweenVisuals(baseScale, 1f, 0.5);
            };

            bool isPressed = false;
            clone.GuiInput += inputEvent =>
            {
                if (inputEvent is InputEventMouseButton mb
                    && mb.ButtonIndex == MouseButton.Left)
                {
                    if (mb.Pressed)
                    {
                        isPressed = true;
                        TweenVisuals(baseScale * 0.95f, 0.8f, 0.5);
                    }
                    else if (isPressed)
                    {
                        isPressed = false;
                        ticked = !ticked;
                        if (tickedImg != null) tickedImg.Visible = ticked;
                        if (notTickedImg != null) notTickedImg.Visible = !ticked;
                        SfxCmd.Play(ticked
                            ? "event:/sfx/ui/clicks/ui_checkbox_on"
                            : "event:/sfx/ui/clicks/ui_checkbox_off");
                        TweenVisuals(baseScale * 1.05f, 1.2f, 0.05);
                        onChanged(ticked);
                    }
                }
            };

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
    /// Creates a slider + editable value input. When a game NSlider source is
    /// available, clones it for the styled diamond handle and thick track.
    /// Falls back to a plain HSlider. Value label is editable (click to type).
    /// Layout matches the game: value on left, slider on right.
    /// </summary>
    private static Control CreateGameSlider(FeatureConfig config, ConfigEntry entry)
    {
        bool isInt = entry.Type == ConfigEntryType.Int;
        double min = entry.Min ?? 0;
        double max = entry.Max ?? 9999;
        double val = isInt ? (int)entry.Value : (float)entry.Value;

        var container = new HBoxContainer { Name = "SliderContainer" };
        container.AddThemeConstantOverride("separation", 0);
        container.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        container.CustomMinimumSize = new Vector2(0, 64);

        string FormatValue(double v) => isInt ? ((int)v).ToString() : v.ToString("F0");

        // Editable value input — styled to match the game's MegaLabel look
        // (transparent background, game font, cream color) but still editable.
        var input = new LineEdit
        {
            Name = "ConfigValue",
            Text = FormatValue(val),
            CustomMinimumSize = new Vector2(25, 40),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            Alignment = HorizontalAlignment.Center,
            Flat = true,
        };
        ApplyGameFont(input, 28);
        input.AddThemeConstantOverride("minimum_character_width", 3);
        var inputColor = Colors.White;
        input.AddThemeColorOverride("font_color", inputColor);
        input.AddThemeColorOverride("font_uneditable_color", inputColor);
        input.AddThemeColorOverride("caret_color", inputColor);
        input.AddThemeColorOverride("font_selected_color", Colors.White);
        input.AddThemeColorOverride("selection_color", new Color(0.4f, 0.5f, 0.6f, 0.5f));
        // Transparent background normally; subtle highlight when focused.
        var emptyBox = new StyleBoxEmpty();
        input.AddThemeStyleboxOverride("normal", emptyBox);
        input.AddThemeStyleboxOverride("read_only", emptyBox);
        var focusBox = new StyleBoxFlat
        {
            BgColor = new Color(0.2f, 0.22f, 0.28f, 0.6f),
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
        };
        input.AddThemeStyleboxOverride("focus", focusBox);
        container.AddChild(input);

        // NSlider's SetValueBasedOnMousePosition assumes MinValue=0, so we
        // normalize to [0, max-min] and offset when reading/writing.
        double range = max - min;
        double normalizedVal = val - min;

        Control sliderNode;
        Node? actualSlider = null; // the NSlider clone, for setting value from text input
        if (_sourceSlider != null)
        {
            var clone = CloneHelper.Clone<Node>(_sourceSlider, CloneHelper.ScriptsOnly)!;
            clone.Set("min_value", 0.0);
            clone.Set("max_value", range);
            clone.Set("step", isInt ? 1.0 : 1.0);

            var cloneCtrl = (Control)clone;
            cloneCtrl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            cloneCtrl.CustomMinimumSize = new Vector2(0, 64);

            clone.Set("value", normalizedVal);

            // NSlider._Ready uses GetNode<Control>("%Handle") but the %
            // unique-name lookup fails on duplicated nodes (owner not set).
            // Re-resolve it after _Ready runs via the Ready signal, then
            // snap the handle to the initial value without animation.
            double initVal = normalizedVal;
            clone.Ready += () =>
            {
                var h = clone.FindChild("Handle", recursive: false, owned: false);
                if (h != null)
                    clone.Set("_handle", h);
                clone.Call("SetValueWithoutAnimation", initVal);
            };

            clone.Connect(Godot.Range.SignalName.ValueChanged, Callable.From<double>(sliderVal =>
            {
                double realVal = sliderVal + min;
                if (isInt)
                    entry.Value = (int)realVal;
                else
                    entry.Value = (float)realVal;
                if (!input.HasFocus())
                    input.Text = FormatValue(realVal);
                config.Save();
            }));

            // Wrap in a MarginContainer so the handle diamond has room to
            // overhang at 0% and 100% without clipping.
            var sliderMargin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(324, 64),
            };
            sliderMargin.AddThemeConstantOverride("margin_left", 20);
            sliderMargin.AddThemeConstantOverride("margin_right", 20);
            sliderMargin.AddChild(cloneCtrl);
            actualSlider = clone;
            sliderNode = sliderMargin;
        }
        else
        {
            // Fallback: plain Godot HSlider
            var slider = new HSlider
            {
                Name = "ConfigSlider",
                MinValue = min,
                MaxValue = max,
                Value = val,
                Step = isInt ? 1 : 1,
                CustomMinimumSize = new Vector2(250, 0),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            slider.ValueChanged += v =>
            {
                if (isInt)
                    entry.Value = (int)v;
                else
                    entry.Value = (float)v;
                if (!input.HasFocus())
                    input.Text = FormatValue(v);
                config.Save();
            };
            sliderNode = slider;
        }
        container.AddChild(sliderNode);

        // Wire editable input → slider sync. Don't clamp here — let the
        // user finish typing. Clamping happens on submit / focus-exit.
        input.TextChanged += text =>
        {
            if (double.TryParse(text, out var v))
            {
                double clamped = Mathf.Clamp(v, min, max);
                if (actualSlider != null)
                    actualSlider.Set("value", clamped - min);
                else
                    ((HSlider)sliderNode).Value = clamped;
            }
        };

        void ClampInputText()
        {
            if (double.TryParse(input.Text, out var v))
            {
                v = Mathf.Clamp(v, min, max);
                input.Text = FormatValue(v);
            }
            else
            {
                input.Text = FormatValue(isInt ? (int)entry.Value : (float)entry.Value);
            }
        }

        input.TextSubmitted += _ => { ClampInputText(); input.ReleaseFocus(); };
        input.FocusExited += ClampInputText;

        // Release input focus when clicking anywhere outside it (e.g. the slider).
        container.GuiInput += inputEvent =>
        {
            if (inputEvent is InputEventMouseButton { Pressed: true } && input.HasFocus())
                input.ReleaseFocus();
        };

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
            {
                bool val = (bool)entry.Value;
                if (last is CheckBox cb)
                {
                    cb.SetPressedNoSignal(val);
                }
                else
                {
                    // Scriptless tickbox clone — toggle visibility of Ticked/NotTicked children
                    var ticked = (last as Control)?.FindChild("Ticked", recursive: true, owned: false) as Control;
                    var notTicked = (last as Control)?.FindChild("NotTicked", recursive: true, owned: false) as Control;
                    if (ticked != null) ticked.Visible = val;
                    if (notTicked != null) notTicked.Visible = !val;
                }
                break;
            }
            case ConfigEntryType.Int:
            case ConfigEntryType.Float:
            {
                if (last is not HBoxContainer sliderBox) break;
                var input = sliderBox.GetNodeOrNull<LineEdit>("ConfigValue");
                bool isInt = entry.Type == ConfigEntryType.Int;
                double v = isInt ? (int)entry.Value : (float)entry.Value;
                double min = entry.Min ?? 0;

                // Find slider: game clone named "Slider" (may be nested in
                // MarginContainer) or fallback "ConfigSlider"
                var slider = sliderBox.FindChild("Slider", recursive: true, owned: false) as Godot.Range
                          ?? sliderBox.GetNodeOrNull<Godot.Range>("ConfigSlider");
                if (slider != null)
                {
                    // Game NSlider uses normalized range [0, max-min]
                    bool isGameSlider = slider.Name == "Slider";
                    slider.Set("value", isGameSlider ? v - min : v);
                }
                if (input != null)
                    input.Text = isInt ? ((int)v).ToString() : v.ToString("F0");
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
