using System;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace dubiousQOL.UI;

/// <summary>
/// Game-styled interactive widget factories. Acquires source nodes
/// automatically from the game's settings screen scene on first use.
/// Falls back to plain Godot widgets if acquisition fails.
/// </summary>
internal static class WidgetHelper
{
    private static Node? _cachedTickbox;
    private static Node? _cachedSlider;
    private static Node? _cachedLabel;
    private static Node? _cachedButton;
    private static bool _coldAcquired;

    private const string TickboxPath =
        "ScrollContainer/Mask/Clipper/GeneralSettings/VBoxContainer/CommonTooltips/SettingsTickbox";
    private const string SliderPath =
        "ScrollContainer/Mask/Clipper/SoundSettings/VBoxContainer/MasterVolume/MasterVolumeSlider/Slider";
    private const string LabelPath =
        "ScrollContainer/Mask/Clipper/GeneralSettings/VBoxContainer/FastMode/Label";
    private const string ButtonPath =
        "ScrollContainer/Mask/Clipper/GeneralSettings/VBoxContainer/ResetGameplay/ResetGameplayButton";

    /// <summary>
    /// Cold-acquires source nodes by instantiating the game's settings screen
    /// scene and cloning widgets from known paths. Called automatically on
    /// first widget creation.
    /// </summary>
    private static void TryColdAcquire()
    {
        if (_coldAcquired) return;
        _coldAcquired = true;
        try
        {
            var scene = ResourceLoader.Load<PackedScene>(
                SceneHelper.GetScenePath("screens/settings_screen"),
                null, ResourceLoader.CacheMode.Reuse);
            if (scene == null) return;
            var instance = scene.Instantiate<Node>(PackedScene.GenEditState.Disabled);
            if (instance == null) return;
            try
            {
                var tickbox = instance.GetNodeOrNull(TickboxPath);
                if (tickbox != null)
                    _cachedTickbox ??= tickbox.Duplicate(CloneHelper.VisualOnly);

                var slider = instance.GetNodeOrNull(SliderPath);
                if (slider != null)
                    _cachedSlider ??= slider.Duplicate(CloneHelper.ScriptsOnly);

                var label = instance.GetNodeOrNull(LabelPath);
                if (label != null)
                    _cachedLabel ??= label.Duplicate(CloneHelper.Full);

                var button = instance.GetNodeOrNull(ButtonPath);
                if (button != null)
                    _cachedButton ??= button.Duplicate(CloneHelper.FullNoSignals);
            }
            finally
            {
                instance.QueueFree();
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"WidgetHelper cold acquire: {e.Message}");
        }
    }

    /// <summary>
    /// Game-styled tickbox. Clones the visual shell of NTickbox (no scripts) and
    /// handles toggle logic manually — NCommonTooltipsTickbox.OnUntick NullRefs
    /// outside the settings screen, so scripts are stripped.
    /// Falls back to a plain CheckBox if the source isn't available.
    /// </summary>
    public static Control CreateGameTickbox(bool initialValue, Action<bool> onChanged)
    {
        TryColdAcquire();
        if (_cachedTickbox != null)
        {
            var clone = CloneHelper.Clone<Control>(_cachedTickbox, CloneHelper.VisualOnly)!;
            clone.CustomMinimumSize = new Vector2(320, 64);
            clone.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
            clone.MouseFilter = Control.MouseFilterEnum.Stop;

            // SelectionReticle is the gold focus border — hidden in the live
            // tree but can default to visible in a cold clone.
            var reticle = clone.FindChild("SelectionReticle", recursive: false, owned: false) as Control;
            if (reticle != null) reticle.Visible = false;

            var tickedImg = clone.FindChild("Ticked", recursive: true, owned: false) as Control;
            var notTickedImg = clone.FindChild("NotTicked", recursive: true, owned: false) as Control;
            var visuals = clone.FindChild("TickboxVisuals", recursive: true, owned: false) as Control;
            var baseScale = visuals?.Scale ?? Vector2.One;
            if (visuals?.Material is ShaderMaterial shared)
                visuals.Material = (ShaderMaterial)shared.Duplicate();
            var hsv = visuals?.Material as ShaderMaterial;

            bool ticked = initialValue;
            if (tickedImg != null) tickedImg.Visible = ticked;
            if (notTickedImg != null) notTickedImg.Visible = !ticked;

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
            clone.MouseExited += () => TweenVisuals(baseScale, 1f, 0.5);

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
    /// Programmatically sets a tickbox's value without firing the callback.
    /// Works on both the game-styled clone and the plain CheckBox fallback.
    /// </summary>
    public static void SetTickboxValue(Control tickbox, bool value)
    {
        if (tickbox is CheckBox cb)
        {
            cb.SetPressedNoSignal(value);
            return;
        }
        var ticked = tickbox.FindChild("Ticked", recursive: true, owned: false) as Control;
        var notTicked = tickbox.FindChild("NotTicked", recursive: true, owned: false) as Control;
        if (ticked != null) ticked.Visible = value;
        if (notTicked != null) notTicked.Visible = !value;
    }

    /// <summary>
    /// Game-styled slider with editable value input. Clones the NSlider for the
    /// styled diamond handle and thick track. Falls back to a plain HSlider.
    /// Layout: value input on left, slider on right.
    ///
    /// Child node names for programmatic access:
    ///   "ConfigValue" — LineEdit with current value
    ///   "Slider"      — NSlider clone (normalized to [0, max-min]), inside a MarginContainer
    ///   "ConfigSlider" — fallback HSlider (real range [min, max])
    /// </summary>
    public static Control CreateGameSlider(double value, double min, double max,
        bool isInt, Action<double> onChanged)
    {
        TryColdAcquire();
        var container = new HBoxContainer { Name = "SliderContainer" };
        container.AddThemeConstantOverride("separation", 0);
        container.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        container.CustomMinimumSize = new Vector2(0, 64);

        double currentVal = value;
        string FormatValue(double v) => isInt ? ((int)v).ToString() : v.ToString("F0");

        var input = new LineEdit
        {
            Name = "ConfigValue",
            Text = FormatValue(value),
            CustomMinimumSize = new Vector2(25, 40),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            Alignment = HorizontalAlignment.Center,
            Flat = true,
        };
        var font = FontHelper.Load("kreon-regular");
        if (font != null) input.AddThemeFontOverride("font", font);
        input.AddThemeFontSizeOverride("font_size", 28);
        input.AddThemeConstantOverride("minimum_character_width", 3);
        input.AddThemeColorOverride("font_color", Colors.White);
        input.AddThemeColorOverride("font_uneditable_color", Colors.White);
        input.AddThemeColorOverride("caret_color", Colors.White);
        input.AddThemeColorOverride("font_selected_color", Colors.White);
        input.AddThemeColorOverride("selection_color", Theme.InputSelection);
        var emptyBox = new StyleBoxEmpty();
        input.AddThemeStyleboxOverride("normal", emptyBox);
        input.AddThemeStyleboxOverride("read_only", emptyBox);
        input.AddThemeStyleboxOverride("focus", StyleHelper.MakeStyleBox(Theme.InputFocusBg, cornerRadius: 4));
        container.AddChild(input);

        // NSlider's SetValueBasedOnMousePosition assumes MinValue=0, so we
        // normalize to [0, max-min] and offset when reading/writing.
        double range = max - min;
        double normalizedVal = value - min;

        Control sliderNode;
        Node? actualSlider = null;
        if (_cachedSlider != null)
        {
            var clone = CloneHelper.Clone<Node>(_cachedSlider, CloneHelper.ScriptsOnly)!;
            clone.Set("min_value", 0.0);
            clone.Set("max_value", range);
            clone.Set("step", isInt ? 1.0 : 1.0);

            var cloneCtrl = (Control)clone;
            cloneCtrl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            cloneCtrl.CustomMinimumSize = new Vector2(0, 64);

            clone.Set("value", normalizedVal);

            // NSlider._Ready uses GetNode<Control>("%Handle") but the %
            // unique-name lookup fails on duplicated nodes (owner not set).
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
                currentVal = realVal;
                if (!input.HasFocus())
                    input.Text = FormatValue(realVal);
                onChanged(realVal);
            }));

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
            var slider = new HSlider
            {
                Name = "ConfigSlider",
                MinValue = min,
                MaxValue = max,
                Value = value,
                Step = isInt ? 1 : 1,
                CustomMinimumSize = new Vector2(250, 0),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            slider.ValueChanged += v =>
            {
                currentVal = v;
                if (!input.HasFocus())
                    input.Text = FormatValue(v);
                onChanged(v);
            };
            sliderNode = slider;
        }
        container.AddChild(sliderNode);

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
                input.Text = FormatValue(currentVal);
            }
        }

        input.TextSubmitted += _ => { ClampInputText(); input.ReleaseFocus(); };
        input.FocusExited += ClampInputText;

        container.GuiInput += inputEvent =>
        {
            if (inputEvent is InputEventMouseButton { Pressed: true } && input.HasFocus())
                input.ReleaseFocus();
        };

        return container;
    }

    /// <summary>
    /// Programmatically sets a slider's value without firing the callback.
    /// Works on both the game-styled clone and the plain HSlider fallback.
    /// </summary>
    public static void SetSliderValue(Control sliderContainer, double value, double min, bool isInt)
    {
        if (sliderContainer is not HBoxContainer hbox) return;
        var input = hbox.GetNodeOrNull<LineEdit>("ConfigValue");

        var slider = hbox.FindChild("Slider", recursive: true, owned: false) as Godot.Range
                  ?? hbox.GetNodeOrNull<Godot.Range>("ConfigSlider");
        if (slider != null)
        {
            bool isGameSlider = slider.Name == "Slider";
            slider.Set("value", isGameSlider ? value - min : value);
        }
        if (input != null)
            input.Text = isInt ? ((int)value).ToString() : value.ToString("F0");
    }

    /// <summary>
    /// Game-styled MegaRichTextLabel for settings-row labels.
    /// Falls back to a plain Label if the source isn't available.
    /// </summary>
    public static Control CreateGameLabel(string text)
    {
        TryColdAcquire();
        if (_cachedLabel != null)
        {
            var clone = CloneHelper.Clone<Control>(_cachedLabel, CloneHelper.Full)!;
            if (clone is RichTextLabel rtl)
                rtl.Text = text;
            clone.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            clone.CustomMinimumSize = new Vector2(0, 64);
            return clone;
        }
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
    /// Game-styled hex button (NButton clone). The button text is set via
    /// CallDeferred after _Ready populates MegaLabel internals.
    /// Falls back to a plain Button if the source isn't available.
    /// Returns (control, isGameStyled) so callers know which event to wire.
    /// </summary>
    public static (Control button, bool isGameStyled) CreateGameButton(string text)
    {
        TryColdAcquire();
        if (_cachedButton != null)
        {
            var clone = CloneHelper.Clone<Control>(_cachedButton, CloneHelper.FullNoSignals)!;
            clone.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            clone.Ready += () =>
            {
                var lbl = clone.FindChild("Label", recursive: true, owned: false);
                lbl?.Call("SetTextAutoSize", text);
            };
            return (clone, true);
        }
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(240, 48),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        var font = FontHelper.Load("kreon-regular");
        if (font != null) btn.AddThemeFontOverride("font", font);
        btn.AddThemeFontSizeOverride("font_size", 24);
        return (btn, false);
    }
}
