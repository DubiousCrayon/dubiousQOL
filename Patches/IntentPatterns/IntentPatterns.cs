using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.sts2.Core.Nodes.TopBar;

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

[HarmonyPatch(typeof(NTopBarBossIcon), "OnActEntered")]
public static class PatchBossIconIntentPatterns
{
    private static ulong _wiredInstanceId;

    [HarmonyPostfix]
    public static void Postfix(NTopBarBossIcon __instance)
    {
        if (!IntentPatternsConfig.Instance.Enabled) return;

        var id = __instance.GetInstanceId();
        if (_wiredInstanceId == id) return;
        _wiredInstanceId = id;

        try
        {
            __instance.GuiInput += inputEvent =>
            {
                if (inputEvent is InputEventMouseButton mb
                    && mb.ButtonIndex == MouseButton.Middle
                    && mb.Pressed)
                {
                    ShowBossViewer(__instance);
                    __instance.GetViewport().SetInputAsHandled();
                }
            };
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"IntentPatterns boss wire: {e.Message}");
        }
    }

    private static void ShowBossViewer(NTopBarBossIcon bossIcon)
    {
        try
        {
            var runState = bossIcon._runState;
            if (runState?.Act == null) return;

            IntentPatternsData.EnsureLoaded();

            var encounter = runState.Act.BossEncounter;
            if (encounter == null) return;

            // Collect patterns for ALL monsters in this encounter
            string encounterSlug = encounter.Id.Entry.ToLowerInvariant();
            var sections = new List<MonsterSection>();
            foreach (var monster in encounter.AllPossibleMonsters)
            {
                var entry = monster.Id.Entry;
                if (!IntentPatternsData.HasEnrichment(entry)) continue;

                var patterns = IntentPatternsData.ResolveEnrichmentOnly(entry);
                if (patterns.Count == 0) continue;

                sections.Add(new MonsterSection
                {
                    Name = monster.Title.GetFormattedText() ?? entry,
                    MonsterEntry = entry,
                    EncounterSlug = encounterSlug,
                    Patterns = patterns,
                });
            }

            if (sections.Count == 0) return;

            string bossName = encounter.Title.GetFormattedText() ?? sections[0].MonsterEntry;

            var modal = ModalHelper.GetModal();
            if (modal == null) return;

            NHoverTipSet.shouldBlockHoverTips = true;
            var viewer = new IntentPatternsViewer(bossName, sections);
            viewer.TreeExited += () => NHoverTipSet.shouldBlockHoverTips = false;
            modal.Add(viewer, showBackstop: false);
        }
        catch (Exception e)
        {
            NHoverTipSet.shouldBlockHoverTips = false;
            MainFile.Logger.Warn($"IntentPatterns boss open: {e.Message}");
        }
    }
}
