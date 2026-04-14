using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace dubiousQOL.Patches;

/// <summary>
/// Injects a "Mod Configuration (dubiousQOL)" row into the settings screen, right next
/// to BaseLib's equivalent. Clicking Open Config pops a modal with tickboxes for each
/// feature flag; changes write through DubiousConfig.Save() immediately. Most flags
/// take effect on the next invocation of their hook; UnifiedSavePath needs a restart.
/// </summary>
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
        if (vbox.HasNode(RowName)) return; // idempotent

        var divider = vbox.GetNodeOrNull<ColorRect>("SendFeedbackDivider");
        var feedback = vbox.GetNodeOrNull<MarginContainer>("SendFeedback");
        var modding = vbox.GetNodeOrNull<MarginContainer>("Modding");
        if (divider == null || feedback == null || modding == null)
        {
            MainFile.Logger.Warn("ModConfigUI: expected settings rows missing, skipping injection.");
            return;
        }

        // Duplicate flag 15 = Signals|Groups|Scripts|UseInstantiation. Keeping
        // scripts is necessary — NClickableControl's script is what provides
        // click detection; without it the button would be visually fine but dead.
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

        // Set text AFTER the nodes are in the tree. Deferred so any _Ready
        // label init runs first and we win the last write.
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
    public static void Open(NSettingsScreen settingsScreen)
    {
        try
        {
            var modal = NModalContainer.Instance;
            if (modal == null) return;
            var panel = BuildPanel();
            modal.Add(panel);
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
        frame.OffsetLeft = -280; frame.OffsetRight = 280;
        frame.OffsetTop = -220; frame.OffsetBottom = 220;
        root.AddChild(frame);

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 24);
        pad.AddThemeConstantOverride("margin_right", 24);
        pad.AddThemeConstantOverride("margin_top", 20);
        pad.AddThemeConstantOverride("margin_bottom", 20);
        frame.AddChild(pad);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 14);
        pad.AddChild(vbox);

        var title = new Label
        {
            Text = "dubiousQOL — Feature Toggles",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 22);
        vbox.AddChild(title);

        var hint = new Label
        {
            Text = "Toggle features on/off. Most apply live; Unified Save Path requires a restart.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 14);
        hint.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f));
        vbox.AddChild(hint);

        AddToggle(vbox, "Act Name Display", DubiousConfig.ActNameDisplay, v => DubiousConfig.ActNameDisplay = v);
        AddToggle(vbox, "Win Streak Display", DubiousConfig.WinStreakDisplay, v => DubiousConfig.WinStreakDisplay = v);
        AddToggle(vbox, "Deck Search", DubiousConfig.DeckSearch, v => DubiousConfig.DeckSearch = v);
        AddToggle(vbox, "Rarity Display (hover tips)", DubiousConfig.RarityDisplay, v => DubiousConfig.RarityDisplay = v);
        AddToggle(vbox, "Unified Save Path (restart required)", DubiousConfig.UnifiedSavePath, v => DubiousConfig.UnifiedSavePath = v);
        AddToggle(vbox, "Skip Splash Screen", DubiousConfig.SkipSplash, v => DubiousConfig.SkipSplash = v);
        AddToggle(vbox, "Incoming Damage Display", DubiousConfig.IncomingDamageDisplay, v => DubiousConfig.IncomingDamageDisplay = v);

        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(140, 36) };
        close.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        close.Pressed += () => NModalContainer.Instance?.Clear();
        vbox.AddChild(close);

        return root;
    }

    private static void AddToggle(Container parent, string label, bool initial, Action<bool> setter)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        var lbl = new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        lbl.AddThemeFontSizeOverride("font_size", 16);
        row.AddChild(lbl);

        var cb = new CheckBox { ButtonPressed = initial };
        cb.Toggled += v => { setter(v); DubiousConfig.Save(); };
        row.AddChild(cb);

        parent.AddChild(row);
    }
}
