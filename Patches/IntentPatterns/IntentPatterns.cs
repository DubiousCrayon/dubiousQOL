using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Rooms;

using dubiousQOL.UI;

namespace dubiousQOL.Patches;

[HarmonyPatch(typeof(NCombatRoom), nameof(NCombatRoom.AddCreature))]
public static class PatchIntentPatternsWiring
{
    [HarmonyPostfix]
    public static void Postfix(Creature creature)
    {
        try
        {
            if (!IntentPatternsConfig.Instance.Enabled) return;
            if (creature.Side != CombatSide.Enemy) return;

            var nCreature = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (nCreature?.Hitbox == null) return;

            nCreature.Hitbox.GuiInput += inputEvent =>
            {
                if (inputEvent is InputEventMouseButton mb
                    && mb.ButtonIndex == MouseButton.Middle
                    && mb.Pressed)
                {
                    ShowViewer(nCreature);
                    nCreature.Hitbox.GetViewport().SetInputAsHandled();
                }
            };
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"IntentPatterns wire: {e.Message}");
        }
    }

    private static void ShowViewer(NCreature nCreature)
    {
        try
        {
            IntentPatternsData.EnsureLoaded();
            var patterns = IntentPatternsData.Resolve(nCreature);
            if (patterns.Count == 0) return;

            string creatureName = nCreature.Entity?.Name ?? "Unknown";
            string monsterEntry = nCreature.Entity?.Monster?.Id.Entry ?? "";

            var modal = ModalHelper.GetModal();
            if (modal == null) return;

            NHoverTipSet.shouldBlockHoverTips = true;
            var viewer = new IntentPatternsViewer(creatureName, patterns, monsterEntry);
            viewer.TreeExited += () => NHoverTipSet.shouldBlockHoverTips = false;
            modal.Add(viewer, showBackstop: false);
        }
        catch (Exception e)
        {
            NHoverTipSet.shouldBlockHoverTips = false;
            MainFile.Logger.Warn($"IntentPatterns open: {e.Message}");
        }
    }
}
