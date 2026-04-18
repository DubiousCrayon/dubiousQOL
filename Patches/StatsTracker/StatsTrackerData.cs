using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Platform;
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
    public static event Action? Updated;

    internal const ulong UnattributedId = 0;

    // Set by CreatureCmd.Damage prefix so the LoseHpInternal postfix can
    // attribute the killing blow when CombatHistory.DamageReceived is blocked
    // by the IsEnding guard.
    internal static Creature? CurrentDealer;

    private static bool _installed;
    private static bool _hookedHistory;
    private static int _processedCount;

    // Tracks which players applied damage-dealing debuffs to each enemy,
    // so null-dealer damage (Poison/Haunt/Strangle ticks) can be attributed
    // to the correct player(s). Populated from PowerReceivedEntry.
    private static readonly Dictionary<Creature, HashSet<ulong>> _debuffDealers = new();

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
        }
        catch (Exception e) { MainFile.Logger.Warn($"StatsTrackerData install: {e.Message}"); }
    }

    private static void OnCombatSetUp()
    {
        Combat.Reset();
        _processedCount = 0;
        _debuffDealers.Clear();
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
        Run.Reset();
        Act.Reset();
        Combat.Reset();
        _processedCount = 0;
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
        if (p.Power is not (PoisonPower or HauntPower or StranglePower)) return;
        var receiver = p.Actor;
        if (receiver.Side != CombatSide.Enemy) return;
        var applier = p.Applier;
        var owner = applier.IsPlayer ? applier.Player
                  : applier.IsPet ? applier.PetOwner
                  : null;
        if (owner == null) return;
        if (!_debuffDealers.TryGetValue(receiver, out var set))
            _debuffDealers[receiver] = set = new HashSet<ulong>();
        set.Add(owner.NetId);
    }

    private static void ProcessDamage(DamageReceivedEntry d)
    {
        // UnblockedDamage = actual HP removed (excludes overkill and blocked damage).
        // A 40-damage hit on a 10 HP enemy records 10, not 40.
        var hpRemoved = d.Result.UnblockedDamage;
        if (hpRemoved <= 0) return;

        if (d.Receiver != null && d.Receiver.Side == CombatSide.Enemy)
        {
            var dealer = d.Dealer;
            if (dealer != null)
            {
                var owner = dealer.IsPlayer ? dealer.Player
                          : dealer.IsPet ? dealer.PetOwner
                          : null;
                if (owner != null)
                    AddAll(owner.NetId, s => s.DamageDealt += hpRemoved);
            }
            else
            {
                AttributeNullDealerDamage(d.Receiver, hpRemoved);
            }
        }

        if (d.Receiver != null && d.Receiver.IsPlayer && d.Receiver.Player != null)
            AddAll(d.Receiver.Player.NetId, s => s.HpLost += hpRemoved);
    }

    private static void AttributeNullDealerDamage(Creature receiver, int total)
    {
        if (_debuffDealers.TryGetValue(receiver, out var appliers) && appliers.Count > 0)
        {
            int share = total / appliers.Count;
            int remainder = total % appliers.Count;
            int i = 0;
            foreach (var netId in appliers)
            {
                int amt = share + (i < remainder ? 1 : 0);
                AddAll(netId, s => s.DamageDealt += amt);
                i++;
            }
        }
        else
        {
            AddAll(UnattributedId, s => s.DamageDealt += total);
        }
    }

    private static void ProcessBlock(BlockGainedEntry b)
    {
        if (b.Amount <= 0) return;
        if (b.Receiver != null && b.Receiver.IsPlayer && b.Receiver.Player != null)
            AddAll(b.Receiver.Player.NetId, s => s.BlockGained += b.Amount);
    }

    private static void AddAll(ulong netId, Action<DmPlayerStats> apply)
    {
        apply(Combat.Get(netId));
        apply(Act.Get(netId));
        apply(Run.Get(netId));
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
                    AddAll(owner.NetId, s => s.DamageDealt += hpRemoved);
                else
                    AddAll(UnattributedId, s => s.DamageDealt += hpRemoved);
            }
            else
            {
                AttributeNullDealerDamage(receiver, hpRemoved);
            }
        }

        if (receiver.IsPlayer && receiver.Player != null)
            AddAll(receiver.Player.NetId, s => s.HpLost += hpRemoved);

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
    public static void Prefix(Creature? dealer) => StatsTrackerData.CurrentDealer = dealer;
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

internal sealed class DmSidecarPlayer
{
    [JsonPropertyName("netId")] public string NetId { get; set; } = "";
    [JsonPropertyName("character")] public string? Character { get; set; }
    [JsonPropertyName("damageDealt")] public long DamageDealt { get; set; }
    [JsonPropertyName("blockGained")] public long BlockGained { get; set; }
    [JsonPropertyName("hpLost")] public long HpLost { get; set; }
}

internal sealed class DmSidecar
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("runTurns")] public int RunTurns { get; set; }
    [JsonPropertyName("players")] public List<DmSidecarPlayer> Players { get; set; } = new();
}

internal static class StatsTrackerIO
{
    private const string Suffix = ".dm.json";

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string? SidecarPath(long startTime)
    {
        try
        {
            int profileId = SaveManager.Instance.CurrentProfileId;
            // Same path discipline as MapHistory: sidecars live OUTSIDE history/ so the
            // run-history loader doesn't try to deserialize them as corrupt runs.
            string userPath = UserDataPathProvider.GetProfileScopedPath(profileId, UserDataPathProvider.SavesDir);
            string abs = ProjectSettings.GlobalizePath(userPath);
            return Path.Combine(abs, "damage_meter", startTime + Suffix);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"StatsTracker path resolve: {e.Message}");
            return null;
        }
    }

    internal static void Write(long startTime)
    {
        var path = SidecarPath(startTime);
        if (path == null) return;
        if (StatsTrackerData.Run.ByPlayer.Count == 0) return;

        var side = new DmSidecar { RunTurns = StatsTrackerData.Run.PlayerTurns };
        var state = RunManager.Instance?.DebugOnlyGetState();
        foreach (var kv in StatsTrackerData.Run.ByPlayer)
        {
            string? character = null;
            try { character = state?.GetPlayer(kv.Key)?.Character?.Title.GetFormattedText(); }
            catch { }
            side.Players.Add(new DmSidecarPlayer
            {
                NetId = kv.Key.ToString(),
                Character = character,
                DamageDealt = kv.Value.DamageDealt,
                BlockGained = kv.Value.BlockGained,
                HpLost = kv.Value.HpLost,
            });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(side, _opts));
    }

    internal static DmSidecar? Read(long startTime)
    {
        try
        {
            var path = SidecarPath(startTime);
            if (path == null || !File.Exists(path)) return null;
            return JsonSerializer.Deserialize<DmSidecar>(File.ReadAllText(path), _opts);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"StatsTracker sidecar read: {e.Message}");
            return null;
        }
    }
}
