using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace dubiousQOL.Patches;

/// <summary>
/// Shows predicted incoming damage ("X DMG", red) and unblockable HP loss
/// ("-X HP", purple) next to the player's combat health bar.
///
/// Damage = enemy AttackIntent totals + hand end-of-turn damage cards
/// (Burn/Decay/Infection/Toxic, go through block) − predicted block.
/// Enemy intents already inherit target-side mods (Intangible, TungstenRod,
/// Guarded/Covered, HardToKill, DiamondDiadem, UndyingSigil, Tank/Intercept)
/// because AttackIntent.GetSingleDamage routes through Hook.ModifyDamage,
/// which iterates BOTH dealer- and target-side hook listeners. Hand damage
/// cards use DamageVar.PreviewValue for the same reason.
///
/// HP loss = cards that inflict direct HP loss (Beckon/BadLuck/Regret) —
/// unblockable, but still go through Hook.ModifyHpLostAfterOsty.
///
/// BeatingRemnant caps total HP lost per turn (combat damage + direct HP
/// loss) via ModifyHpLostAfterOsty, so when it's present we cap the display
/// sum at 20 − damageReceivedThisTurn (typically 20 during the player turn).
/// </summary>
[HarmonyPatch]
public static class PatchIncomingDamageDisplay
{
    private const string ContainerName = "DubiousIncomingDamage";
    private const float GapTopOfBar = -25f;
    private const float GapRightOfBar = -10f;
    private const float DamageLeanDegrees = -12f;
    private const string FontPath = "res://dubiousQOL/fonts/fightkid.ttf";

    private static readonly Color DamageColor = new(0.95f, 0.15f, 0.15f);
    private static readonly Color HpLossColor = new(0.72f, 0.42f, 0.95f);

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
        root.Size = new Vector2(160, 60);

        var damage = MakeLabel("Damage", 26, DamageColor);
        damage.Size = new Vector2(160, 28);
        // Pivot at the bottom-left so the baseline next to the HP bar stays put
        // and the top of the number tips toward the bar.
        damage.PivotOffset = new Vector2(0, 15);
        damage.RotationDegrees = DamageLeanDegrees;
        root.AddChild(damage);

        var hpLoss = MakeLabel("HpLoss", 22, HpLossColor);
        hpLoss.Size = new Vector2(160, 26);
        // Pivot at the bottom-left so the baseline next to the HP bar stays put
        // and the top of the number tips toward the bar.
        hpLoss.PivotOffset = new Vector2(0, 15);
        hpLoss.RotationDegrees = DamageLeanDegrees;
        root.AddChild(hpLoss);
        return root;
    }

    private static RichTextLabel MakeLabel(string name, int fontSize, Color color)
    {
        var lbl = new RichTextLabel
        {
            Name = name,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            BbcodeEnabled = true,
            FitContent = false,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
            Visible = false,
        };
        var font = ResourceLoader.Load<Font>(FontPath, null, ResourceLoader.CacheMode.Reuse);
        if (font != null) lbl.AddThemeFontOverride("normal_font", font);
        lbl.AddThemeFontSizeOverride("normal_font_size", fontSize);
        lbl.AddThemeColorOverride("default_color", color);
        lbl.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
        lbl.AddThemeConstantOverride("outline_size", 5);
        lbl.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.7f));
        lbl.AddThemeConstantOverride("shadow_offset_x", 1);
        lbl.AddThemeConstantOverride("shadow_offset_y", 2);
        return lbl;
    }

    private static void UpdateDisplay(Control container, Creature player)
    {
        var damageLabel = container.GetNode<RichTextLabel>("Damage");
        var hpLossLabel = container.GetNode<RichTextLabel>("HpLoss");

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
            hpLossLabel.Visible = false;
            return;
        }

        int enemyDamage = 0;
        var targets = new[] { player };
        foreach (var enemy in combatState.GetCreaturesOnSide(CombatSide.Enemy))
        {
            if (!enemy.IsAlive) continue;
            var monster = enemy.Monster;
            if (monster == null) continue;
            foreach (var intent in monster.NextMove.Intents)
            {
                if (intent is AttackIntent attack)
                    enemyDamage += attack.GetTotalDamage(targets, enemy);
            }
        }

        // Cards in hand that trigger at end of turn split into two buckets:
        // block-affected damage (Burn/Decay/Infection/Toxic) and unblockable
        // HP loss (Beckon/BadLuck/Regret).
        int handDamage = 0;
        int hpLoss = 0;
        var hand = player.Player?.PlayerCombatState?.Hand;
        if (hand != null)
        {
            int handSize = hand.Cards.Count;
            foreach (var card in hand.Cards)
            {
                if (!card.HasTurnEndInHandEffect) continue;
                switch (card)
                {
                    case Beckon:
                    case BadLuck:
                        hpLoss += ReadPreviewHpLoss(card, player);
                        break;
                    case Regret:
                        // Regret's damage is one per card in hand (including itself).
                        hpLoss += handSize;
                        break;
                    case Burn:
                    case Decay:
                    case Infection:
                    case Toxic:
                        handDamage += ReadPreviewDamage(card, player);
                        break;
                }
            }
        }

        int predictedBlock = player.Block + PredictEndOfTurnBlockGain(player);
        int incoming = Math.Max(0, enemyDamage + handDamage - predictedBlock);

        // Osty (Necrobinder familiar) absorbs damage after block, up to its
        // current HP. Only applies to powered attacks — hand damage cards are
        // unpowered and bypass Osty. Null/dead Osty = no absorption.
        var osty = player.Player?.Osty;
        if (osty != null && osty.IsAlive)
            incoming = Math.Max(0, incoming - osty.CurrentHp);

        // BeatingRemnant caps total HP lost this turn (combat damage +
        // direct HP loss) via ModifyHpLostAfterOsty. HP-loss cards trigger
        // first at turn end, so give hpLoss priority over incoming damage.
        int brCap = GetBeatingRemnantCap(player);
        if (brCap >= 0)
        {
            hpLoss = Math.Min(hpLoss, brCap);
            incoming = Math.Min(incoming, Math.Max(0, brCap - hpLoss));
        }
        // U+2009 THIN SPACE: narrows only the gap between the number and "DMG"
        // without affecting kerning inside "DMG" itself.
        damageLabel.Text = incoming + "\u2009DMG";
        damageLabel.Visible = incoming > 0;
        damageLabel.Position = new Vector2(0, 2);

        hpLossLabel.Text = "[font=res://dubiousQOL/fonts/SANDEN.ttf]-[/font]" + hpLoss + "\u2009HP";
        hpLossLabel.Visible = hpLoss > 0;
        // Promote HP loss to the top slot when no damage is showing so it
        // doesn't float in empty space below an invisible label.
        hpLossLabel.Position = new Vector2(0, incoming > 0 ? 34 : 6);
    }

    // Block gained by end-of-turn triggers that fire before enemy attacks
    // resolve. Excluded: AfterBlockCleared sources (HornCleat, CaptainsWheel)
    // fire only if block drops to zero mid-attack; start-of-turn relics
    // (Anchor) are already in player.Block. Orichalcum variants gate on
    // block == 0 at BeforeTurnEndVeryEarly, which runs before anything here
    // adds block, so reading current player.Block is the correct predicate.
    private static int PredictEndOfTurnBlockGain(Creature player)
    {
        int gain = 0;
        try
        {
            foreach (var power in player.Powers)
            {
                if (power is PlatingPower plating) gain += plating.Amount;
            }
            var orbQueue = player.Player?.PlayerCombatState?.OrbQueue;
            if (orbQueue != null)
            {
                foreach (var orb in orbQueue.Orbs)
                {
                    if (orb is FrostOrb frost) gain += (int)frost.PassiveVal;
                }
            }
            var relics = player.Player?.Relics;
            if (relics != null)
            {
                int handSize = player.Player?.PlayerCombatState?.Hand?.Cards.Count ?? 0;
                bool blockIsZero = player.Block == 0;
                foreach (var relic in relics)
                {
                    switch (relic)
                    {
                        case CloakClasp cloak:
                            gain += (int)(handSize * cloak.DynamicVars.Block.BaseValue);
                            break;
                        case Orichalcum ori when blockIsZero:
                            gain += (int)ori.DynamicVars.Block.BaseValue;
                            break;
                        case FakeOrichalcum fake when blockIsZero:
                            gain += (int)fake.DynamicVars.Block.BaseValue;
                            break;
                        // RippleBasin self-tracks with Status: Active at turn start,
                        // flipped to Normal by AfterCardPlayed the moment any attack
                        // resolves, so Active == "no attack played yet this turn".
                        case RippleBasin ripple when ripple.Status == RelicStatus.Active:
                            gain += (int)ripple.DynamicVars.Block.BaseValue;
                            break;
                    }
                }
            }
        }
        catch { }
        return gain;
    }

    // Reads the preview value for a card's in-hand damage (Burn/Decay/
    // Infection/Toxic). UpdateCardPreview runs enchantments + full
    // Hook.ModifyDamage pipeline, so Intangible/TungstenRod/Guarded/etc.
    // on the target are applied consistently with enemy-intent numbers.
    private static int ReadPreviewDamage(CardModel card, Creature target)
    {
        try
        {
            var v = card.DynamicVars.Damage;
            v.UpdateCardPreview(card, CardPreviewMode.None, target, true);
            return Math.Max(0, (int)v.PreviewValue);
        }
        catch { return (int)card.DynamicVars.Damage.BaseValue; }
    }

    private static int ReadPreviewHpLoss(CardModel card, Creature target)
    {
        try
        {
            var v = card.DynamicVars.HpLoss;
            v.UpdateCardPreview(card, CardPreviewMode.None, target, true);
            return Math.Max(0, (int)v.PreviewValue);
        }
        catch { return (int)card.DynamicVars.HpLoss.BaseValue; }
    }

    // Returns the remaining HP-loss headroom if BeatingRemnant is equipped,
    // or -1 if the relic isn't present. BR's counter is private, so we read
    // it via Traverse. During the player turn it's typically 0 (reset in
    // BeforeSideTurnStart), so the effective cap is 20.
    private static int GetBeatingRemnantCap(Creature player)
    {
        var relics = player.Player?.Relics;
        if (relics == null) return -1;
        foreach (var relic in relics)
        {
            if (relic is BeatingRemnant br)
            {
                decimal alreadyTaken = 0m;
                try
                {
                    alreadyTaken = Traverse.Create(br)
                        .Field("_damageReceivedThisTurn")
                        .GetValue<decimal>();
                }
                catch { }
                int max = (int)br.DynamicVars["MaxHpLoss"].BaseValue;
                return Math.Max(0, max - (int)alreadyTaken);
            }
        }
        return -1;
    }
}
