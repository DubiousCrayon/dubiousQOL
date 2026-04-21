using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using dubiousQOL.UI;
using dubiousQOL.Utilities;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.ValueProps;

namespace dubiousQOL.Patches;

/// <summary>
/// Aggregates per-player damage dealt, block gained, and damage taken across three
/// scopes (Combat/Act/Run) by tailing CombatHistory.Changed and counting player
/// turns from CombatManager.TurnStarted. Sidecar JSON lives alongside map_history
/// for symmetry with MapHistory.
///
/// Multiplayer: each peer has its own CombatHistory instance, but entries are
/// populated identically on all peers because synchronized actions run
/// CreatureCmd.Damage()/etc. locally on each peer. That means tailing local
/// history gives correct per-player totals without any net-side aggregation.
/// </summary>
internal sealed class DmPlayerStats
{
    public long DamageDealt;
    public long BlockGained;
    public long HpLost;
    public readonly Dictionary<string, long> DamageBySource = new();
    public readonly Dictionary<string, long> BlockBySource = new();
    public readonly Dictionary<string, long> HpLostBySource = new();
}

internal sealed class DmScopeStats
{
    public readonly Dictionary<ulong, DmPlayerStats> ByPlayer = new();
    public int PlayerTurns;

    public DmPlayerStats Get(ulong netId)
    {
        if (!ByPlayer.TryGetValue(netId, out var s))
            ByPlayer[netId] = s = new DmPlayerStats();
        return s;
    }

    public void Reset()
    {
        ByPlayer.Clear();
        PlayerTurns = 0;
    }
}

internal static class StatsTrackerData
{
    public static readonly DmScopeStats Combat = new();
    public static readonly DmScopeStats Act = new();
    public static readonly DmScopeStats Run = new();
    public static readonly List<DmCombatSnapshot> CombatSnapshots = new();
    public static event Action? Updated;

    internal static void NotifyUpdated() => Updated?.Invoke();

    internal const ulong UnattributedId = 0;

    // Set by CreatureCmd.Damage prefix so the LoseHpInternal postfix can
    // attribute the killing blow when CombatHistory.DamageReceived is blocked
    // by the IsEnding guard.
    internal static Creature? CurrentDealer;
    // Set by CreatureCmd.Damage prefix for source tracking on killing blows.
    internal static CardModel? CurrentCardSource;
    // Set by FrostOrb prefix so ProcessBlock can distinguish orb block from
    // relic/power block (both pass null CardPlay to GainBlock).
    internal static string? CurrentBlockSource;
    // Set by orb damage prefixes (Lightning/Dark) so ResolveDamageSource can
    // label orb damage instead of falling through to the player character name.
    internal static string? CurrentDamageSource;
    // Snapshot of CurrentDamageSource taken at the start of each CreatureCmd.Damage
    // call. Using a separate field prevents async potion OnUse methods from losing
    // their source when the Harmony postfix fires before the damage call.
    internal static string? PendingDamageSource;

    private static bool _installed;
    private static bool _hookedHistory;
    private static int _processedCount;
    // Set by RestoreMidRun so the next RunStarted (which fires for saved
    // runs too) skips ResetAll instead of wiping the just-restored data.
    internal static bool RestoredFromSidecar;

    // Tracks which players applied damage-dealing debuffs to each enemy,
    // keyed by creature → list of (netId, powerName) pairs. Used to attribute
    // null-dealer damage (Poison/Haunt/Strangle ticks) with source names.
    private static readonly Dictionary<Creature, List<(ulong netId, string powerName)>> _debuffSources = new();

    // --- Source-attribution patch tables ---
    // Adding a new relic/potion/orb = append one tuple. The manual Harmony
    // patches in PatchSourceAttribution wire shared prefix/postfix methods
    // that set/clear the side-channel fields.

    private static readonly (Type type, string method)[] DamageRelicPatches =
    {
        (typeof(FestivePopper), nameof(FestivePopper.AfterPlayerTurnStart)),
        (typeof(ForgottenSoul), nameof(ForgottenSoul.AfterCardExhausted)),
        (typeof(Kusarigama), nameof(Kusarigama.AfterCardPlayed)),
        (typeof(LetterOpener), nameof(LetterOpener.AfterCardPlayed)),
        (typeof(MercuryHourglass), nameof(MercuryHourglass.AfterPlayerTurnStart)),
        (typeof(Metronome), nameof(Metronome.AfterOrbChanneled)),
        (typeof(MrStruggles), nameof(MrStruggles.AfterPlayerTurnStart)),
        (typeof(ParryingShield), nameof(ParryingShield.AfterTurnEnd)),
        (typeof(ScreamingFlagon), nameof(ScreamingFlagon.BeforeTurnEnd)),
        (typeof(StoneCalendar), nameof(StoneCalendar.BeforeTurnEnd)),
        (typeof(Tingsha), nameof(Tingsha.AfterCardDiscarded)),
    };

    private static readonly (Type type, string method)[] DamagePotionPatches =
    {
        (typeof(ExplosiveAmpoule), nameof(ExplosiveAmpoule.OnUse)),
        (typeof(FirePotion), nameof(FirePotion.OnUse)),
        (typeof(PotionShapedRock), nameof(PotionShapedRock.OnUse)),
    };

    // Powers that deal damage with the player as dealer and no cardSource.
    // Same label-lookup pattern as orbs; prefix only (async methods).
    private static readonly (Type type, string method, string label)[] DamagePowerPatches =
    {
        (typeof(ReflectPower), nameof(ReflectPower.AfterDamageReceived), "Reflect"),
        (typeof(ThornsPower), nameof(ThornsPower.BeforeDamageReceived), "Thorns"),
    };

    private static readonly (Type type, string method, string label)[] DamageOrbPatches =
    {
        (typeof(LightningOrb), nameof(LightningOrb.Passive), "Lightning Orb"),
        (typeof(LightningOrb), nameof(LightningOrb.Evoke), "Lightning Orb"),
        (typeof(DarkOrb), nameof(DarkOrb.Evoke), "Dark Orb"),
        (typeof(GlassOrb), nameof(GlassOrb.Passive), "Glass Orb"),
        (typeof(GlassOrb), nameof(GlassOrb.Evoke), "Glass Orb"),
    };

    private static readonly (Type type, string method)[] BlockPotionPatches =
    {
        (typeof(BlockPotion), nameof(BlockPotion.OnUse)),
        (typeof(Fortifier), nameof(Fortifier.OnUse)),
    };

    private static readonly (Type type, string method, string label)[] BlockOrbPatches =
    {
        (typeof(FrostOrb), nameof(FrostOrb.Passive), "Frost Orb"),
        (typeof(FrostOrb), nameof(FrostOrb.Evoke), "Frost Orb"),
    };

    // Powers that grant block with null CardPlay (not via a card play).
    // Prefix only (async methods — postfix fires too early).
    private static readonly (Type type, string method, string label)[] BlockPowerPatches =
    {
        (typeof(AfterimagePower), nameof(AfterimagePower.AfterCardPlayed), "Afterimage"),
    };

    // Lookup for label injection — populated during PatchSourceAttribution,
    // read by prefix methods via __originalMethod.
    private static readonly Dictionary<MethodBase, string> _damageOrbLabels = new();
    private static readonly Dictionary<MethodBase, string> _damagePowerLabels = new();
    private static readonly Dictionary<MethodBase, string> _blockOrbLabels = new();
    private static readonly Dictionary<MethodBase, string> _blockPowerLabels = new();

    // Shared prefix/postfix methods used by manual Harmony patches.
    public static void RelicDamagePrefix(RelicModel __instance) =>
        CurrentDamageSource = __instance.Title.GetFormattedText();
    public static void PotionDamagePrefix(PotionModel __instance) =>
        CurrentDamageSource = __instance.Title.GetFormattedText();
    public static void DamageSourcePostfix() =>
        CurrentDamageSource = null;
    public static void OrbDamagePrefix(MethodBase __originalMethod)
    {
        if (_damageOrbLabels.TryGetValue(__originalMethod, out var label))
            CurrentDamageSource = label;
    }
    public static void PowerDamagePrefix(MethodBase __originalMethod)
    {
        if (_damagePowerLabels.TryGetValue(__originalMethod, out var label))
            CurrentDamageSource = label;
    }
    public static void PotionBlockPrefix(PotionModel __instance) =>
        CurrentBlockSource = __instance.Title.GetFormattedText();
    public static void OrbBlockPrefix(MethodBase __originalMethod)
    {
        if (_blockOrbLabels.TryGetValue(__originalMethod, out var label))
            CurrentBlockSource = label;
    }
    public static void PowerBlockPrefix(MethodBase __originalMethod)
    {
        if (_blockPowerLabels.TryGetValue(__originalMethod, out var label))
            CurrentBlockSource = label;
    }
    public static void BlockSourcePostfix() =>
        CurrentBlockSource = null;

    internal static void PatchSourceAttribution(Harmony harmony)
    {
        var relicPfx = new HarmonyMethod(AccessTools.Method(typeof(StatsTrackerData), nameof(RelicDamagePrefix)));
        var potionPfx = new HarmonyMethod(AccessTools.Method(typeof(StatsTrackerData), nameof(PotionDamagePrefix)));
        var dmgPost = new HarmonyMethod(AccessTools.Method(typeof(StatsTrackerData), nameof(DamageSourcePostfix)));

        foreach (var (type, method) in DamageRelicPatches)
            harmony.Patch(AccessTools.Method(type, method), prefix: relicPfx, postfix: dmgPost);

        // Potion OnUse methods are async Task — the Harmony postfix fires at
        // the first yield, not when the method completes. Omit postfix; the
        // PendingDamageSource snapshot in PatchDamageDealer.Prefix handles cleanup.
        foreach (var (type, method) in DamagePotionPatches)
            harmony.Patch(AccessTools.Method(type, method), prefix: potionPfx);

        // Power damage methods are async — prefix only, same as potions.
        var powerDmgPfx = new HarmonyMethod(AccessTools.Method(typeof(StatsTrackerData), nameof(PowerDamagePrefix)));
        foreach (var (type, method, label) in DamagePowerPatches)
        {
            var original = AccessTools.Method(type, method);
            _damagePowerLabels[original] = label;
            harmony.Patch(original, prefix: powerDmgPfx);
        }

        var orbDmgPfx = new HarmonyMethod(AccessTools.Method(typeof(StatsTrackerData), nameof(OrbDamagePrefix)));
        foreach (var (type, method, label) in DamageOrbPatches)
        {
            var original = AccessTools.Method(type, method);
            _damageOrbLabels[original] = label;
            harmony.Patch(original, prefix: orbDmgPfx, postfix: dmgPost);
        }

        var orbBlkPfx = new HarmonyMethod(AccessTools.Method(typeof(StatsTrackerData), nameof(OrbBlockPrefix)));
        var blkPost = new HarmonyMethod(AccessTools.Method(typeof(StatsTrackerData), nameof(BlockSourcePostfix)));
        foreach (var (type, method, label) in BlockOrbPatches)
        {
            var original = AccessTools.Method(type, method);
            _blockOrbLabels[original] = label;
            harmony.Patch(original, prefix: orbBlkPfx, postfix: blkPost);
        }

        // Block powers: prefix only (async methods — postfix fires too early).
        var powerBlkPfx = new HarmonyMethod(AccessTools.Method(typeof(StatsTrackerData), nameof(PowerBlockPrefix)));
        foreach (var (type, method, label) in BlockPowerPatches)
        {
            var original = AccessTools.Method(type, method);
            _blockPowerLabels[original] = label;
            harmony.Patch(original, prefix: powerBlkPfx);
        }

        // Block potions: prefix only (async OnUse — postfix fires too early).
        var potionBlkPfx = new HarmonyMethod(AccessTools.Method(typeof(StatsTrackerData), nameof(PotionBlockPrefix)));
        foreach (var (type, method) in BlockPotionPatches)
            harmony.Patch(AccessTools.Method(type, method), prefix: potionBlkPfx);
    }

    internal static DmScopeStats ForScope(DmScope scope) => scope switch
    {
        DmScope.Combat => Combat,
        DmScope.Act => Act,
        _ => Run,
    };

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            RunManager.Instance.RunStarted += _ => ResetAll();
            RunManager.Instance.ActEntered += ResetAct;
            CombatManager.Instance.CombatSetUp += _ => OnCombatSetUp();
            CombatManager.Instance.CombatEnded += OnCombatEnded;
        }
        catch (Exception e) { MainFile.Logger.Warn($"StatsTrackerData install: {e.Message}"); }
    }

    private static void OnCombatEnded(CombatRoom room)
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            var scope = StatsTrackerIO.ToSidecarScope(Combat, state);
            string? encounter = null;
            try { encounter = room.Encounter?.Id.Entry; } catch { }
            CombatSnapshots.Add(new DmCombatSnapshot
            {
                Act = state?.CurrentActIndex ?? 0,
                Floor = state?.ActFloor ?? 0,
                Encounter = encounter,
                RoomType = room.RoomType.ToString(),
                PlayerTurns = scope.PlayerTurns,
                Players = scope.Players,
            });
        }
        catch (Exception e) { MainFile.Logger.Warn($"StatsTracker combat snapshot: {e.Message}"); }
    }

    internal static void OnGameSaved()
    {
        if (!StatsTrackerConfig.Instance.Enabled) return;
        try { StatsTrackerIO.WriteMidRun(); }
        catch (Exception e) { MainFile.Logger.Warn($"StatsTracker mid-run save: {e.Message}"); }
    }

    private static void OnCombatSetUp()
    {
        Combat.Reset();
        _processedCount = 0;
        _debuffSources.Clear();
        SourceIconResolver.ClearCache();
        if (!_hookedHistory)
        {
            // History is a singleton property on CombatManager (constructed in its
            // private ctor), so the same instance lives across combats; subscribe once.
            CombatManager.Instance.History.Changed += OnHistoryChanged;
            CombatManager.Instance.TurnStarted += OnTurnStarted;
            _hookedHistory = true;
        }
        Updated?.Invoke();
    }

    private static void OnTurnStarted(CombatState state)
    {
        if (state == null || state.CurrentSide != CombatSide.Player) return;
        Combat.PlayerTurns++;
        Act.PlayerTurns++;
        Run.PlayerTurns++;
        Updated?.Invoke();
    }

    private static void ResetAll()
    {
        if (RestoredFromSidecar)
        {
            RestoredFromSidecar = false;
            return;
        }
        Run.Reset();
        Act.Reset();
        Combat.Reset();
        _processedCount = 0;
        CombatSnapshots.Clear();
        SourceIconResolver.ClearCache();
        StatsTrackerIO.DeleteMidRunSidecar();
        Updated?.Invoke();
    }

    private static void ResetAct()
    {
        Act.Reset();
        Updated?.Invoke();
    }

    private static void OnHistoryChanged()
    {
        try
        {
            var history = CombatManager.Instance?.History;
            if (history == null) return;
            // Entries is IEnumerable<CombatHistoryEntry>; materialize for indexed tailing.
            // Changed fires after each add, so the list is typically small per call.
            var entries = history.Entries.ToList();
            if (entries.Count < _processedCount) _processedCount = 0; // Clear() detected
            for (int i = _processedCount; i < entries.Count; i++)
                Process(entries[i]);
            _processedCount = entries.Count;
            Updated?.Invoke();
        }
        catch (Exception e) { MainFile.Logger.Warn($"StatsTrackerData history: {e.Message}"); }
    }

    private static void Process(CombatHistoryEntry entry)
    {
        switch (entry)
        {
            case PowerReceivedEntry p: ProcessPower(p); break;
            case DamageReceivedEntry d: ProcessDamage(d); break;
            case BlockGainedEntry b: ProcessBlock(b); break;
        }
    }

    private static void ProcessPower(PowerReceivedEntry p)
    {
        if (p.Amount <= 0 || p.Applier == null) return;
        if (p.Power is not (PoisonPower or HauntPower or StranglePower or DoomPower)) return;
        var receiver = p.Actor;
        if (receiver.Side != CombatSide.Enemy) return;
        var applier = p.Applier;
        var owner = applier.IsPlayer ? applier.Player
                  : applier.IsPet ? applier.PetOwner
                  : null;
        if (owner == null) return;
        string powerName = p.Power.GetType().Name.Replace("Power", "");
        if (!_debuffSources.TryGetValue(receiver, out var list))
            _debuffSources[receiver] = list = new List<(ulong, string)>();
        // Avoid duplicate entries for the same player+power combo.
        bool found = false;
        foreach (var entry in list)
            if (entry.netId == owner.NetId && entry.powerName == powerName) { found = true; break; }
        if (!found) list.Add((owner.NetId, powerName));
    }

    // WaterfallGiant's invincible phase sets HP to 999,999,999. Any damage entry
    // that somehow reports more than this threshold is cosmetic/sentinel and must be dropped.
    private const int SentinelHpThreshold = 100_000;

    private static void ProcessDamage(DamageReceivedEntry d)
    {
        // UnblockedDamage = actual HP removed (excludes overkill and blocked damage).
        // A 40-damage hit on a 10 HP enemy records 10, not 40.
        var hpRemoved = d.Result.UnblockedDamage;
        if (hpRemoved <= 0) return;

        // Drop sentinel-sized entries (e.g. CreatureCmd.Kill on 999,999,999 HP creatures).
        if (hpRemoved > SentinelHpThreshold) return;

        if (d.Receiver != null && d.Receiver.Side == CombatSide.Enemy)
        {
            // Skip cosmetic damage against creatures showing infinite HP (Door, WaterfallGiant).
            if (d.Receiver.ShowsInfiniteHp) return;

            var dealer = d.Dealer;
            if (dealer != null)
            {
                var owner = dealer.IsPlayer ? dealer.Player
                          : dealer.IsPet ? dealer.PetOwner
                          : null;
                if (owner != null)
                {
                    string source = ResolveDamageSource(d.CardSource, dealer);
                    AddDamage(owner.NetId, hpRemoved, source);
                }
            }
            else
            {
                AttributeNullDealerDamage(d.Receiver, hpRemoved);
            }
        }

        if (d.Receiver != null && d.Receiver.IsPlayer && d.Receiver.Player != null)
        {
            string hpSource = ResolveHpLostSource(d.Dealer, d.CardSource);
            AddHpLost(d.Receiver.Player.NetId, hpRemoved, hpSource);
        }
    }

    private static string ResolveDamageSource(CardModel? cardSource, Creature? dealer)
    {
        if (cardSource != null)
        {
            try { return cardSource.Title; }
            catch { return cardSource.GetType().Name; }
        }
        if (PendingDamageSource != null) return PendingDamageSource;
        if (dealer != null)
        {
            try
            {
                if (dealer.Monster != null) return dealer.Monster.Title.GetFormattedText();
                if (dealer.IsPlayer) return dealer.Player?.Character?.Title.GetFormattedText() ?? "Player";
            }
            catch { }
        }
        return "Other";
    }

    private static string ResolveHpLostSource(Creature? dealer, CardModel? cardSource)
    {
        if (dealer?.Monster != null)
        {
            try { return dealer.Monster.Title.GetFormattedText(); }
            catch { return dealer.Monster.GetType().Name; }
        }
        // For self-damage (player hitting themselves), prefer the specific source
        // (card, relic, power) over the generic character name.
        if (cardSource != null)
        {
            try { return cardSource.Title; }
            catch { return cardSource.GetType().Name; }
        }
        if (PendingDamageSource != null) return PendingDamageSource;
        if (dealer != null && dealer.IsPlayer)
        {
            try { return dealer.Player?.Character?.Title.GetFormattedText() ?? "Self"; }
            catch { return "Self"; }
        }
        return "Other";
    }

    private static void AttributeNullDealerDamage(Creature receiver, int total, string? overrideSource = null)
    {
        if (_debuffSources.TryGetValue(receiver, out var sources) && sources.Count > 0)
        {
            int share = total / sources.Count;
            int remainder = total % sources.Count;
            int i = 0;
            foreach (var (netId, powerName) in sources)
            {
                int amt = share + (i < remainder ? 1 : 0);
                AddDamage(netId, amt, overrideSource ?? powerName);
                i++;
            }
        }
        else
        {
            AddDamage(UnattributedId, total, overrideSource ?? "Other");
        }
    }

    private static void ProcessBlock(BlockGainedEntry b)
    {
        if (b.Amount <= 0) return;
        if (b.Receiver != null && b.Receiver.IsPlayer && b.Receiver.Player != null)
        {
            string source;
            if (b.CardPlay?.Card != null)
            {
                try { source = b.CardPlay.Card.Title; }
                catch { source = b.CardPlay.Card.GetType().Name; }
            }
            else
            {
                source = CurrentBlockSource ?? "Other";
            }
            AddBlock(b.Receiver.Player.NetId, b.Amount, source);
        }
    }

    private static void AddDamage(ulong netId, int amount, string source)
    {
        foreach (var scope in new[] { Combat, Act, Run })
        {
            var s = scope.Get(netId);
            s.DamageDealt += amount;
            s.DamageBySource[source] = s.DamageBySource.GetValueOrDefault(source) + amount;
        }
    }

    private static void AddBlock(ulong netId, int amount, string source)
    {
        foreach (var scope in new[] { Combat, Act, Run })
        {
            var s = scope.Get(netId);
            s.BlockGained += amount;
            s.BlockBySource[source] = s.BlockBySource.GetValueOrDefault(source) + amount;
        }
    }

    private static void AddHpLost(ulong netId, int amount, string source)
    {
        foreach (var scope in new[] { Combat, Act, Run })
        {
            var s = scope.Get(netId);
            s.HpLost += amount;
            s.HpLostBySource[source] = s.HpLostBySource.GetValueOrDefault(source) + amount;
        }
    }

    // DoomPower.DoomKill bypasses CreatureCmd.Damage entirely — it calls
    // CreatureCmd.Kill which zeros HP via LoseHpInternal without creating a
    // DamageReceivedEntry. Credit the creature's remaining HP as damage
    // dealt to whoever applied Doom, using the same _debuffDealers map.
    internal static void ProcessDoomKill(IReadOnlyList<Creature> creatures)
    {
        foreach (var creature in creatures)
        {
            int hp = creature.CurrentHp;
            if (hp <= 0 || creature.Side != CombatSide.Enemy) continue;
            AttributeNullDealerDamage(creature, hp, "Doom");
        }
        Updated?.Invoke();
    }

    // Supplements CombatHistory tailing for the killing blow on the last
    // primary enemy. CombatHistory.DamageReceived is guarded by
    // `IsInProgress && !IsEnding`, and IsEnding flips to true the instant
    // LoseHpInternal zeroes the last primary enemy's HP — so the final
    // DamageReceivedEntry is never created and our tailing never sees it.
    // This method is called from a LoseHpInternal postfix only when that
    // guard would have blocked the entry.
    internal static void ProcessDirectHpLoss(Creature receiver, DamageResult result)
    {
        if (!CombatManager.Instance.IsInProgress || !CombatManager.Instance.IsEnding) return;

        var hpRemoved = result.UnblockedDamage;
        if (hpRemoved <= 0) return;

        if (receiver.Side == CombatSide.Enemy)
        {
            var dealer = CurrentDealer;
            if (dealer != null)
            {
                var owner = dealer.IsPlayer ? dealer.Player
                          : dealer.IsPet ? dealer.PetOwner
                          : null;
                if (owner != null)
                {
                    string source = ResolveDamageSource(CurrentCardSource, dealer);
                    AddDamage(owner.NetId, hpRemoved, source);
                }
                else
                    AddDamage(UnattributedId, hpRemoved, "Other");
            }
            else
            {
                AttributeNullDealerDamage(receiver, hpRemoved);
            }
        }

        if (receiver.IsPlayer && receiver.Player != null)
        {
            string hpSource = ResolveHpLostSource(CurrentDealer, CurrentCardSource);
            AddHpLost(receiver.Player.NetId, hpRemoved, hpSource);
        }

        Updated?.Invoke();
    }
}

[HarmonyPatch(typeof(RunHistoryUtilities), nameof(RunHistoryUtilities.CreateRunHistoryEntry))]
public static class PatchStatsTrackerWriteSidecar
{
    [HarmonyPostfix]
    public static void Postfix(SerializableRun run)
    {
        if (!StatsTrackerConfig.Instance.Enabled) return;
        try { StatsTrackerIO.Write(run.StartTime); }
        catch (Exception e) { MainFile.Logger.Warn($"StatsTracker sidecar: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
    new[] { typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel) })]
public static class PatchDamageDealer
{
    [HarmonyPrefix]
    public static void Prefix(Creature? dealer, CardModel? cardSource)
    {
        StatsTrackerData.CurrentDealer = dealer;
        StatsTrackerData.CurrentCardSource = cardSource;
        // Snapshot the side-channel source (set by potion/relic/orb prefixes)
        // before it can be cleared by an async postfix firing at the first yield.
        StatsTrackerData.PendingDamageSource = StatsTrackerData.CurrentDamageSource;
        StatsTrackerData.CurrentDamageSource = null;
    }
}

[HarmonyPatch(typeof(Creature), nameof(Creature.LoseHpInternal))]
public static class PatchLoseHpInternal
{
    [HarmonyPostfix]
    public static void Postfix(Creature __instance, DamageResult __result)
    {
        if (!StatsTrackerConfig.Instance.Enabled) return;
        try { StatsTrackerData.ProcessDirectHpLoss(__instance, __result); }
        catch (Exception e) { MainFile.Logger.Warn($"StatsTracker LoseHp: {e.Message}"); }
    }
}

// Doom kills bypass CreatureCmd.Damage entirely — capture remaining HP
// before DoomKill zeros it via CreatureCmd.Kill.
[HarmonyPatch(typeof(DoomPower), nameof(DoomPower.DoomKill))]
public static class PatchDoomKill
{
    [HarmonyPrefix]
    public static void Prefix(IReadOnlyList<Creature> creatures)
    {
        if (!StatsTrackerConfig.Instance.Enabled) return;
        try { StatsTrackerData.ProcessDoomKill(creatures); }
        catch (Exception e) { MainFile.Logger.Warn($"StatsTracker DoomKill: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRun))]
public static class PatchSaveRunSidecar
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        StatsTrackerData.OnGameSaved();
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpSavedSinglePlayer))]
public static class PatchRestoreStatsSinglePlayer
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        if (!StatsTrackerConfig.Instance.Enabled) return;
        try { StatsTrackerIO.RestoreMidRun(); }
        catch (Exception e) { MainFile.Logger.Warn($"StatsTracker restore SP: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpSavedMultiPlayer))]
public static class PatchRestoreStatsMultiPlayer
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        if (!StatsTrackerConfig.Instance.Enabled) return;
        try { StatsTrackerIO.RestoreMidRun(); }
        catch (Exception e) { MainFile.Logger.Warn($"StatsTracker restore MP: {e.Message}"); }
    }
}

internal sealed class DmSidecarPlayer
{
    [JsonPropertyName("netId")] public string NetId { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("character")] public string? Character { get; set; }
    [JsonPropertyName("damageDealt")] public long DamageDealt { get; set; }
    [JsonPropertyName("blockGained")] public long BlockGained { get; set; }
    [JsonPropertyName("hpLost")] public long HpLost { get; set; }
    [JsonPropertyName("damageBySource")] public Dictionary<string, long>? DamageBySource { get; set; }
    [JsonPropertyName("blockBySource")] public Dictionary<string, long>? BlockBySource { get; set; }
    [JsonPropertyName("hpLostBySource")] public Dictionary<string, long>? HpLostBySource { get; set; }
}

internal sealed class DmSidecarScope
{
    [JsonPropertyName("playerTurns")] public int PlayerTurns { get; set; }
    [JsonPropertyName("players")] public List<DmSidecarPlayer> Players { get; set; } = new();
}

internal sealed class DmCombatSnapshot
{
    [JsonPropertyName("act")] public int Act { get; set; }
    [JsonPropertyName("floor")] public int Floor { get; set; }
    [JsonPropertyName("encounter")] public string? Encounter { get; set; }
    [JsonPropertyName("roomType")] public string? RoomType { get; set; }
    [JsonPropertyName("playerTurns")] public int PlayerTurns { get; set; }
    [JsonPropertyName("players")] public List<DmSidecarPlayer> Players { get; set; } = new();
}

internal sealed class DmSidecar
{
    [JsonPropertyName("version")] public int Version { get; set; } = 2;
    [JsonPropertyName("runTurns")] public int RunTurns { get; set; }
    [JsonPropertyName("players")] public List<DmSidecarPlayer> Players { get; set; } = new();
    [JsonPropertyName("scopes")] public Dictionary<string, DmSidecarScope>? Scopes { get; set; }
    [JsonPropertyName("combats")] public List<DmCombatSnapshot>? Combats { get; set; }
}

internal static class StatsTrackerIO
{
    private const string Dir = "stats_tracker";
    private const string Suffix = ".stats.json";
    private const string MidRunFile = "current_run.stats.json";

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string? SidecarPath(long startTime) =>
        SidecarIO.ResolvePath(Dir, startTime + Suffix);

    internal static string? MidRunSidecarPath() =>
        SidecarIO.ResolvePath(Dir, MidRunFile);

    internal static DmSidecarScope ToSidecarScope(DmScopeStats scope, RunState? state)
    {
        var result = new DmSidecarScope { PlayerTurns = scope.PlayerTurns };
        foreach (var kv in scope.ByPlayer)
        {
            string? character = null;
            try { character = state?.GetPlayer(kv.Key)?.Character?.Title.GetFormattedText(); }
            catch { }
            string? playerName = null;
            try { playerName = ResolvePlayerName(kv.Key); }
            catch { }
            result.Players.Add(new DmSidecarPlayer
            {
                NetId = kv.Key.ToString(),
                Name = playerName,
                Character = character,
                DamageDealt = kv.Value.DamageDealt,
                BlockGained = kv.Value.BlockGained,
                HpLost = kv.Value.HpLost,
                DamageBySource = kv.Value.DamageBySource.Count > 0 ? new(kv.Value.DamageBySource) : null,
                BlockBySource = kv.Value.BlockBySource.Count > 0 ? new(kv.Value.BlockBySource) : null,
                HpLostBySource = kv.Value.HpLostBySource.Count > 0 ? new(kv.Value.HpLostBySource) : null,
            });
        }
        return result;
    }

    private static string? ResolvePlayerName(ulong netId)
    {
        var net = RunManager.Instance?.NetService;
        if (net == null) return null;
        ulong lookupId = netId;
        if (net.Type == NetGameType.Singleplayer && netId == NetSingleplayerGameService.defaultNetId)
            lookupId = PlatformUtil.GetLocalPlayerId(net.Platform);
        var name = PlatformUtil.GetPlayerName(net.Platform, lookupId);
        return !string.IsNullOrEmpty(name) && name != lookupId.ToString() ? name : null;
    }

    // End-of-run sidecar: full run stats with breakdowns and combat snapshots.
    internal static void Write(long startTime)
    {
        var path = SidecarPath(startTime);
        if (path == null) return;
        if (StatsTrackerData.Run.ByPlayer.Count == 0) return;

        var state = RunManager.Instance?.DebugOnlyGetState();
        var runScope = ToSidecarScope(StatsTrackerData.Run, state);

        var side = new DmSidecar
        {
            RunTurns = StatsTrackerData.Run.PlayerTurns,
            Players = runScope.Players,
            Scopes = new() { ["run"] = runScope },
            Combats = StatsTrackerData.CombatSnapshots.Count > 0
                ? new(StatsTrackerData.CombatSnapshots) : null,
        };

        SidecarIO.WriteJson(path, side, _opts);
        DeleteMidRunSidecar();
    }

    // Mid-run sidecar: Act + Run scopes + combat snapshots so far.
    internal static void WriteMidRun()
    {
        var path = MidRunSidecarPath();
        if (path == null) return;
        if (StatsTrackerData.Run.ByPlayer.Count == 0 && StatsTrackerData.Act.ByPlayer.Count == 0) return;

        var state = RunManager.Instance?.DebugOnlyGetState();
        var side = new DmSidecar
        {
            RunTurns = StatsTrackerData.Run.PlayerTurns,
            Players = ToSidecarScope(StatsTrackerData.Run, state).Players,
            Scopes = new()
            {
                ["run"] = ToSidecarScope(StatsTrackerData.Run, state),
                ["act"] = ToSidecarScope(StatsTrackerData.Act, state),
                ["combat"] = ToSidecarScope(StatsTrackerData.Combat, state),
            },
            Combats = StatsTrackerData.CombatSnapshots.Count > 0
                ? new(StatsTrackerData.CombatSnapshots) : null,
        };

        SidecarIO.WriteJson(path, side, _opts);
    }

    internal static DmSidecar? ReadMidRun()
    {
        var path = MidRunSidecarPath();
        return path != null ? SidecarIO.ReadJson<DmSidecar>(path, _opts) : null;
    }

    internal static void DeleteMidRunSidecar()
    {
        var path = MidRunSidecarPath();
        if (path != null) SidecarIO.TryDelete(path);
    }

    private static void RestoreScope(DmScopeStats target, DmSidecarScope source)
    {
        target.Reset();
        target.PlayerTurns = source.PlayerTurns;
        foreach (var p in source.Players)
        {
            if (!ulong.TryParse(p.NetId, out var netId)) continue;
            var stats = target.Get(netId);
            stats.DamageDealt = p.DamageDealt;
            stats.BlockGained = p.BlockGained;
            stats.HpLost = p.HpLost;
            if (p.DamageBySource != null)
                foreach (var kv in p.DamageBySource) stats.DamageBySource[kv.Key] = kv.Value;
            if (p.BlockBySource != null)
                foreach (var kv in p.BlockBySource) stats.BlockBySource[kv.Key] = kv.Value;
            if (p.HpLostBySource != null)
                foreach (var kv in p.HpLostBySource) stats.HpLostBySource[kv.Key] = kv.Value;
        }
    }

    internal static void RestoreMidRun()
    {
        var sidecar = ReadMidRun();
        if (sidecar?.Scopes == null) return;
        if (sidecar.Scopes.TryGetValue("run", out var runScope))
            RestoreScope(StatsTrackerData.Run, runScope);
        if (sidecar.Scopes.TryGetValue("act", out var actScope))
            RestoreScope(StatsTrackerData.Act, actScope);
        if (sidecar.Scopes.TryGetValue("combat", out var combatScope))
            RestoreScope(StatsTrackerData.Combat, combatScope);
        if (sidecar.Combats != null)
        {
            StatsTrackerData.CombatSnapshots.Clear();
            StatsTrackerData.CombatSnapshots.AddRange(sidecar.Combats);
        }
        // RunStarted fires after SetUpSaved*, so guard against the
        // upcoming ResetAll wiping the data we just restored.
        StatsTrackerData.RestoredFromSidecar = true;
        StatsTrackerData.NotifyUpdated();
    }

    internal static DmSidecar? Read(long startTime)
    {
        var path = SidecarPath(startTime);
        return path != null ? SidecarIO.ReadJson<DmSidecar>(path, _opts) : null;
    }
}
