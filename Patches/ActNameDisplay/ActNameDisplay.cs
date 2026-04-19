using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.sts2.Core.Nodes.TopBar;

using dubiousQOL.UI;

namespace dubiousQOL.Patches;

/// <summary>
/// Shows the current act name (Overgrowth / Underdocks / Hive / Glory) as styled
/// text to the right of the top-bar boss icon. The label is placed as a sibling
/// of BossIcon inside the RoomIcons HBoxContainer so it flows inline with the
/// existing icons instead of overlapping them.
/// </summary>
[HarmonyPatch(typeof(NTopBarBossIcon), "OnActEntered")]
public static class PatchActNameDisplay
{
    private const string WrapperName = "DubiousActNameWrapper";

    [HarmonyPostfix]
    public static void Postfix(NTopBarBossIcon __instance)
    {
        if (!ActNameDisplayConfig.Instance.Enabled) return;
        try { UpdateLabel(__instance); }
        catch (Exception e) { MainFile.Logger.Warn($"ActNameDisplay: {e.Message}"); }
    }

    private static void UpdateLabel(NTopBarBossIcon host)
    {
        var runState = host._runState;
        if (runState?.Act == null) return;

        var parent = host.GetParent(); // RoomIcons HBox
        if (parent == null) return;

        var wrapper = parent.GetNodeOrNull<MarginContainer>(WrapperName);
        MegaLabel? label;
        if (wrapper == null)
        {
            label = ActNameLabel.CreateBlank();
            if (label == null) return;
            label.MinFontSize = 14;
            label.MaxFontSize = 34;
            label.HorizontalAlignment = HorizontalAlignment.Left;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.CustomMinimumSize = new Vector2(0, 80);
            label.SizeFlagsVertical = Control.SizeFlags.Fill;
            label.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
            wrapper = new MarginContainer
            {
                Name = WrapperName,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                SizeFlagsVertical = Control.SizeFlags.Fill,
            };
            wrapper.AddChild(label);
            parent.AddChild(wrapper);
            parent.MoveChild(wrapper, host.GetIndex() + 1);
        }
        else
        {
            label = wrapper.GetNodeOrNull<MegaLabel>(ActNameLabel.DefaultName);
            if (label == null) return;
        }

        var actKey = runState.Act.Id.Entry;
        var title = runState.Act.Title.GetFormattedText();

        var (marginLeft, marginTop) = ActNameLabel.GetMargins(actKey);
        wrapper.AddThemeConstantOverride("margin_left", marginLeft);
        wrapper.AddThemeConstantOverride("margin_top", marginTop);
        wrapper.AddThemeConstantOverride("margin_bottom", 0);

        ActNameLabel.ApplyStyle(label, actKey, title);
    }
}
