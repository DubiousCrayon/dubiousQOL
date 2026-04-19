using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace dubiousQOL.Utilities;

/// <summary>
/// Predicts incoming combat damage and HP loss for a player creature.
///
/// Damage = enemy AttackIntent totals + hand end-of-turn damage cards
/// (Burn/Decay/Infection/Toxic) minus predicted block, accounting for
/// Buffer stacks and Osty absorption.
///
/// HP loss = cards that inflict direct HP loss (Beckon/BadLuck/Regret),
/// unblockable but still subject to BeatingRemnant cap.
///
/// Enemy intents inherit target-side mods (Intangible, TungstenRod, etc.)
/// because AttackIntent.GetSingleDamage routes through Hook.ModifyDamage.
/// Hand damage cards use DamageVar.PreviewValue for the same reason.
/// </summary>
internal static class CombatPredictor
{
    public struct IncomingDamageResult
    {
        public int Damage;
        public int HpLoss;
    }

    /// <summary>
    /// Predicts total incoming damage and HP loss for the current turn.
    /// Returns zero/zero if the player is dead, not in combat, or it's not the player's turn.
    /// </summary>
    public static IncomingDamageResult PredictIncoming(Creature player)
    {
        var result = new IncomingDamageResult();
        var combatState = player.CombatState;

        if (!player.IsAlive || combatState == null || combatState.CurrentSide != CombatSide.Player)
            return result;

        int bufferStacks = player.GetPowerAmount<BufferPower>();

        // Cards in hand that trigger at end of turn: block-affected damage
        // (Burn/Decay/Infection/Toxic) vs unblockable HP loss (Beckon/BadLuck/Regret).
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
                        hpLoss += handSize;
                        break;
                    case Burn:
                    case Decay:
                    case Infection:
                    case Toxic:
                        if (bufferStacks > 0)
                            bufferStacks--;
                        else
                            handDamage += ReadPreviewDamage(card, player);
                        break;
                }
            }
        }

        // Enemy attack hits — each repeat is a separate Buffer instance.
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
                {
                    int singleDmg = attack.GetSingleDamage(targets, enemy);
                    int repeats = attack.Repeats;
                    for (int i = 0; i < repeats; i++)
                    {
                        if (bufferStacks > 0)
                            bufferStacks--;
                        else
                            enemyDamage += singleDmg;
                    }
                }
            }
        }

        int predictedBlock = player.Block + PredictEndOfTurnBlockGain(player);
        int incoming = Math.Max(0, enemyDamage + handDamage - predictedBlock);

        // Osty (Necrobinder familiar) absorbs damage after block.
        var osty = player.Player?.Osty;
        if (osty != null && osty.IsAlive)
            incoming = Math.Max(0, incoming - osty.CurrentHp);

        // BeatingRemnant caps total HP lost this turn. HP-loss cards trigger
        // first at turn end, so give hpLoss priority over incoming damage.
        int brCap = GetBeatingRemnantCap(player);
        if (brCap >= 0)
        {
            hpLoss = Math.Min(hpLoss, brCap);
            incoming = Math.Min(incoming, Math.Max(0, brCap - hpLoss));
        }

        result.Damage = incoming;
        result.HpLoss = hpLoss;
        return result;
    }

    /// <summary>
    /// Predicts block gained by end-of-turn triggers that fire before enemy
    /// attacks resolve. Includes: PlatingPower, FrostOrb passive, CloakClasp,
    /// Orichalcum/FakeOrichalcum (when block=0), RippleBasin (when active).
    /// </summary>
    public static int PredictEndOfTurnBlockGain(Creature player)
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

    /// <summary>
    /// Reads the preview value for a card's in-hand damage (Burn/Decay/etc.).
    /// UpdateCardPreview runs enchantments + full Hook.ModifyDamage pipeline.
    /// </summary>
    public static int ReadPreviewDamage(CardModel card, Creature target)
    {
        try
        {
            var v = card.DynamicVars.Damage;
            v.UpdateCardPreview(card, CardPreviewMode.None, target, true);
            return Math.Max(0, (int)v.PreviewValue);
        }
        catch { return (int)card.DynamicVars.Damage.BaseValue; }
    }

    /// <summary>
    /// Reads the preview value for a card's HP loss (Beckon/BadLuck).
    /// </summary>
    public static int ReadPreviewHpLoss(CardModel card, Creature target)
    {
        try
        {
            var v = card.DynamicVars.HpLoss;
            v.UpdateCardPreview(card, CardPreviewMode.None, target, true);
            return Math.Max(0, (int)v.PreviewValue);
        }
        catch { return (int)card.DynamicVars.HpLoss.BaseValue; }
    }

    /// <summary>
    /// Returns the remaining HP-loss headroom if BeatingRemnant is equipped,
    /// or -1 if the relic isn't present.
    /// </summary>
    public static int GetBeatingRemnantCap(Creature player)
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
