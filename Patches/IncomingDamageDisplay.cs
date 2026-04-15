using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace dubiousQOL.Patches;

/// <summary>
/// Shows predicted incoming damage ("-X" in red) next to the player's combat
/// health bar, plus a status-card count ("+N" in orange) below it. Damage is
/// the sum of all alive enemies' queued AttackIntent totals (hook-modified for
/// vulnerable/weak/etc.) minus the player's current block. StatusIntents feed
/// the orange line only — status cards like Beckon don't reduce HP, but the
/// player still wants to see they're incoming.
/// </summary>
[HarmonyPatch]
public static class PatchIncomingDamageDisplay
{
    private const string ContainerName = "DubiousIncomingDamage";
    private const float GapTopOfBar = -25f;
    private const float GapRightOfBar = -15f;
    private const float DamageLeanDegrees = -12f;
    private const string FontPath = "res://dubiousQOL/fonts/fightkid.ttf";

    // HpBarContainer is the actual sized rectangle behind the HP fill — its
    // Size is set by NHealthBar.SetHpBarContainerSizeWithOffsets, so "just
    // right of the bar" is literally Position.X = parent.Size.X + gap.
    // Note: %HpBarContainer is unique-named inside NHealthBar's OWN scene, so
    // we must first look up %HealthBar (unique in the display's scene), then
    // read its public HpBarContainer property.
    private static Control? GetAnchorBar(NCreatureStateDisplay display)
    {
        var healthBar = display.GetNodeOrNull<NHealthBar>("%HealthBar");
        return healthBar?.HpBarContainer;
    }

    [HarmonyPatch(typeof(NCreatureStateDisplay), "SetCreature")]
    [HarmonyPostfix]
    public static void AfterSetCreature(NCreatureStateDisplay __instance, Creature creature)
    {
        if (!DubiousConfig.IncomingDamageDisplay) return;
        try
        {
            if (creature == null || creature.Side != CombatSide.Player) return;
            var bar = GetAnchorBar(__instance);
            if (bar == null) return;
            if (bar.HasNode(ContainerName)) return;
            var container = CreateContainer();
            bar.AddChild(container);
            UpdateDisplay(container, creature);
        }
        catch (Exception e) { MainFile.Logger.Warn($"IncomingDamageDisplay inject: {e.Message}"); }
    }

    [HarmonyPatch(typeof(NCreatureStateDisplay), "RefreshValues")]
    [HarmonyPostfix]
    public static void AfterRefresh(NCreatureStateDisplay __instance)
    {
        if (!DubiousConfig.IncomingDamageDisplay) return;
        try
        {
            var bar = GetAnchorBar(__instance);
            var container = bar?.GetNodeOrNull<Control>(ContainerName);
            if (container == null) return;
            var creature = Traverse.Create(__instance).Field<Creature>("_creature").Value;
            if (creature == null) return;
            UpdateDisplay(container, creature);
        }
        catch (Exception e) { MainFile.Logger.Warn($"IncomingDamageDisplay refresh: {e.Message}"); }
    }

    private static Control CreateContainer()
    {
        var root = new Control
        {
            Name = ContainerName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipContents = false,
        };
        root.Size = new Vector2(140, 60);

        var damage = MakeLabel("Damage", 26, new Color(0.95f, 0.15f, 0.15f));
        damage.Position = new Vector2(0, 2);
        damage.Size = new Vector2(140, 28);
        // Pivot at the bottom-left so the baseline next to the HP bar stays put
        // and the top of the number tips toward the bar.
        damage.PivotOffset = new Vector2(0, 28);
        damage.RotationDegrees = DamageLeanDegrees;
        root.AddChild(damage);

        var status = MakeLabel("Status", 20, new Color(1f, 0.62f, 0.1f));
        status.Position = new Vector2(0, 34);
        status.Size = new Vector2(140, 24);
        root.AddChild(status);
        return root;
    }

    private static Label MakeLabel(string name, int fontSize, Color color)
    {
        var lbl = new Label
        {
            Name = name,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visible = false,
        };
        var font = ResourceLoader.Load<Font>(FontPath, null, ResourceLoader.CacheMode.Reuse);
        if (font != null) lbl.AddThemeFontOverride("font", font);
        lbl.AddThemeFontSizeOverride("font_size", fontSize);
        lbl.AddThemeColorOverride("font_color", color);
        lbl.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
        lbl.AddThemeConstantOverride("outline_size", 5);
        lbl.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.7f));
        lbl.AddThemeConstantOverride("shadow_offset_x", 1);
        lbl.AddThemeConstantOverride("shadow_offset_y", 2);
        return lbl;
    }

    private static void UpdateDisplay(Control container, Creature player)
    {
        var damageLabel = container.GetNode<Label>("Damage");
        var statusLabel = container.GetNode<Label>("Status");

        // Position relative to parent's current size. HpBarContainer is sized
        // by NHealthBar so barSize.X is the actual visual bar width.
        if (container.GetParent() is Control bar)
        {
            var barSize = bar.Size;
            container.Position = new Vector2(
                barSize.X + GapRightOfBar,
                barSize.Y * 0.5f + GapTopOfBar);
        }

        var combatState = player.CombatState;
        // Only preview during the player's turn — enemy-turn intents resolve
        // immediately and would just flicker.
        if (!player.IsAlive || combatState == null || combatState.CurrentSide != CombatSide.Player)
        {
            damageLabel.Visible = false;
            statusLabel.Visible = false;
            return;
        }

        int totalDamage = 0;
        int statusCards = 0;
        var targets = new[] { player };

        foreach (var enemy in combatState.GetCreaturesOnSide(CombatSide.Enemy))
        {
            if (!enemy.IsAlive) continue;
            var monster = enemy.Monster;
            if (monster == null) continue;
            foreach (var intent in monster.NextMove.Intents)
            {
                if (intent is AttackIntent attack)
                    totalDamage += attack.GetTotalDamage(targets, enemy);
                else if (intent is StatusIntent status)
                    statusCards += status.CardCount;
            }
        }

        int incoming = Math.Max(0, totalDamage - player.Block);
        damageLabel.Text = "-" + incoming;
        damageLabel.Visible = incoming > 0;

        statusLabel.Text = "-" + statusCards;
        statusLabel.Visible = statusCards > 0;
    }
}
