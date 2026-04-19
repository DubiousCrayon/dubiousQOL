using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;

using dubiousQOL.UI;
using dubiousQOL.UI.Custom;
using dubiousQOL.Utilities;

namespace dubiousQOL.Patches;

/// <summary>
/// Shows predicted incoming damage ("X DMG", red) and unblockable HP loss
/// ("-X HP", purple) next to the player's combat health bar.
/// Calculation is delegated to <see cref="CombatPredictor"/>.
/// </summary>
[HarmonyPatch]
public static class PatchIncomingDamageDisplay
{
    private const string ContainerName = "DubiousIncomingDamage";
    private const float GapTopOfBar = -25f;
    private const float GapRightOfBar = -10f;
    private const float DamageLeanDegrees = -12f;
    private const string FontId = "fightkid";

    private static readonly Color DamageColor = new(0.95f, 0.15f, 0.15f);
    private static readonly Color HpLossColor = new(0.72f, 0.42f, 0.95f);

    private static Control? GetAnchorBar(NCreatureStateDisplay display)
    {
        var healthBar = display.GetNodeOrNull<NHealthBar>("%HealthBar");
        return healthBar?.HpBarContainer;
    }

    [HarmonyPatch(typeof(NCreatureStateDisplay), "SetCreature")]
    [HarmonyPostfix]
    public static void AfterSetCreature(NCreatureStateDisplay __instance, Creature creature)
    {
        if (!IncomingDamageDisplayConfig.Instance.Enabled) return;
        try
        {
            if (creature == null || creature.Side != CombatSide.Player) return;
            if (creature.PetOwner != null) return;
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
        if (!IncomingDamageDisplayConfig.Instance.Enabled) return;
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
        root.Size = new Vector2(160, 60);

        var damage = Widgets.CreateStyledRichLabel("Damage", FontId, 26, DamageColor);
        damage.Size = new Vector2(160, 28);
        damage.PivotOffset = new Vector2(0, 15);
        damage.RotationDegrees = DamageLeanDegrees;
        root.AddChild(damage);

        var hpLoss = Widgets.CreateStyledRichLabel("HpLoss", FontId, 22, HpLossColor);
        hpLoss.Size = new Vector2(160, 26);
        hpLoss.PivotOffset = new Vector2(0, 15);
        hpLoss.RotationDegrees = DamageLeanDegrees;
        root.AddChild(hpLoss);
        return root;
    }

    private static void UpdateDisplay(Control container, Creature player)
    {
        var damageLabel = container.GetNode<RichTextLabel>("Damage");
        var hpLossLabel = container.GetNode<RichTextLabel>("HpLoss");

        if (container.GetParent() is Control bar)
        {
            var barSize = bar.Size;
            container.Position = new Vector2(
                barSize.X + GapRightOfBar,
                barSize.Y * 0.5f + GapTopOfBar);
        }

        var combatState = player.CombatState;
        if (!player.IsAlive || combatState == null || combatState.CurrentSide != CombatSide.Player)
        {
            damageLabel.Visible = false;
            hpLossLabel.Visible = false;
            return;
        }

        var prediction = CombatPredictor.PredictIncoming(player);

        // U+2009 THIN SPACE: narrows only the gap between the number and "DMG"
        damageLabel.Text = prediction.Damage + "\u2009DMG";
        damageLabel.Visible = prediction.Damage > 0;
        damageLabel.Position = new Vector2(0, 2);

        hpLossLabel.Text = $"[font={FontHelper.GetPath("sanden")}]-[/font]" + prediction.HpLoss + "\u2009HP";
        hpLossLabel.Visible = prediction.HpLoss > 0;
        hpLossLabel.Position = new Vector2(0, prediction.Damage > 0 ? 34 : 6);
    }
}
