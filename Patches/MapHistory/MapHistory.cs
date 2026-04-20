using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.MapDrawing;
using MegaCrit.Sts2.Core.Saves.Runs;

using dubiousQOL.Utilities;

namespace dubiousQOL.Patches;

/// <summary>
/// Captures the ActMap topology and map drawings for each act the player enters, then
/// writes a sidecar JSON next to the run history file on run end. Sidecar lives at
/// {history dir}/{StartTime}.maps.json and is keyed by the same StartTime the game uses
/// for the history entry itself. The run history UI reads the sidecar to display saved
/// maps per act after a run ends.
///
/// Capture timing: Harmony prefix on RunManager.EnterAct. At entry, State.Map is still
/// the outgoing act's map and NMapScreen.Drawings still holds the outgoing drawings;
/// GenerateMap (called later in the same frame) overwrites both. We snapshot here to
/// avoid losing the old act before the new one replaces it.
/// </summary>
public static class MapHistoryCapture
{
    // Keyed by act index at the time of capture. Later acts overwrite earlier entries
    // for the same index if the game regenerates a map (e.g., Golden Compass relic).
    private static readonly Dictionary<int, CapturedAct> _cache = new();

    internal static IReadOnlyDictionary<int, CapturedAct> Cache => _cache;

    internal static void Reset() => _cache.Clear();

    /// <summary>
    /// Reads RunManager._startTime so we can write incremental sidecars mid-run.
    /// Returns 0 if unavailable (caller should skip the write).
    /// </summary>
    private static long GetStartTime()
    {
        try { return Traverse.Create(RunManager.Instance).Field("_startTime").GetValue<long>(); }
        catch { return 0; }
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterAct))]
    public static class PatchEnterAct
    {
        [HarmonyPrefix]
        public static void Prefix(int currentActIndex)
        {
            if (!MapHistoryConfig.Instance.Enabled) return;
            try
            {
                var state = Traverse.Create(RunManager.Instance).Property("State").GetValue();
                if (state == null) return;
                var map = Traverse.Create(state).Property("Map").GetValue();
                if (map == null) return; // first-act entry; nothing to snapshot
                int capturedIndex = Traverse.Create(state).Property("CurrentActIndex").GetValue<int>();

                var serialMap = SerializableActMap.FromActMap((MegaCrit.Sts2.Core.Map.ActMap)map);
                var drawings = NRun.Instance?.GlobalUi.MapScreen.Drawings.GetSerializableMapDrawings();
                var visited = Traverse.Create(state).Property("VisitedMapCoords").GetValue<System.Collections.Generic.IReadOnlyList<MapCoord>>();
                var visitedList = visited != null ? new List<MapCoord>(visited) : new List<MapCoord>();
                string? bossId = null, secondBossId = null;
                try
                {
                    var act = Traverse.Create(state).Property("Act").GetValue() as ActModel;
                    if (act != null)
                    {
                        bossId = act.BossEncounter?.Id.ToString();
                        secondBossId = act.SecondBossEncounter?.Id.ToString();
                    }
                }
                catch { }
                _cache[capturedIndex] = new CapturedAct(serialMap, drawings, visitedList, bossId, secondBossId);

                // Persist incrementally so captures survive game restarts.
                try { MapHistoryIO.WriteIncremental(GetStartTime(), _cache); }
                catch (Exception ie) { MainFile.Logger.Warn($"MapHistory incremental write: {ie.Message}"); }
            }
            catch (Exception e) { MainFile.Logger.Warn($"MapHistory capture: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    public static class PatchCleanUp
    {
        [HarmonyPostfix]
        public static void Postfix() => Reset();
    }

    [HarmonyPatch(typeof(RunHistoryUtilities), nameof(RunHistoryUtilities.CreateRunHistoryEntry))]
    public static class PatchCreateRunHistoryEntry
    {
        [HarmonyPostfix]
        public static void Postfix(SerializableRun run)
        {
            if (!MapHistoryConfig.Instance.Enabled) return;
            try
            {
                // Merge the current act's map from the finalized SerializableRun — the
                // only place the final act's ActMap is preserved is run.Acts[current].SavedMap.
                var finalActs = new Dictionary<int, CapturedAct>(_cache);

                // Recover acts persisted to disk during previous sessions (save/quit/resume).
                MapHistoryIO.MergeFromDisk(run.StartTime, finalActs);

                int currentIdx = run.CurrentActIndex;
                if (currentIdx >= 0 && currentIdx < run.Acts.Count)
                {
                    var finalMap = run.Acts[currentIdx].SavedMap;
                    if (finalMap != null)
                    {
                        // SerializableRoomSet stores BossId/SecondBossId as ModelId, so the
                        // sidecar gets the same canonical strings the live capture path uses.
                        var rooms = run.Acts[currentIdx].SerializableRooms;
                        string? bossId = rooms?.BossId?.ToString();
                        string? secondBossId = rooms?.SecondBossId?.ToString();
                        finalActs[currentIdx] = new CapturedAct(finalMap, run.MapDrawings, new List<MapCoord>(run.VisitedMapCoords), bossId, secondBossId);
                    }
                }
                if (finalActs.Count == 0) return;

                MapHistoryIO.Write(run.StartTime, finalActs);
            }
            catch (Exception e) { MainFile.Logger.Warn($"MapHistory sidecar write: {e.Message}"); }
            finally { Reset(); }
        }
    }
}

internal sealed record CapturedAct(SerializableActMap Map, SerializableMapDrawings? Drawings, List<MapCoord> VisitedMapCoords, string? BossEncounterId, string? SecondBossEncounterId);

internal sealed class MapHistorySidecar
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("acts")] public List<MapHistorySidecarAct> Acts { get; set; } = new();
}

internal sealed class MapHistorySidecarAct
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("map")] public SerializableActMap Map { get; set; } = null!;
    [JsonPropertyName("drawings")]
    [JsonConverter(typeof(SerializableMapDrawingsJsonConverter))]
    public SerializableMapDrawings? Drawings { get; set; }
    [JsonPropertyName("visited")] public List<MapCoord> VisitedMapCoords { get; set; } = new();
    // Encounter IDs needed to populate stub Act._rooms.Boss/SecondBoss when rendering;
    // NBossMapPoint._Ready throws if these aren't set on the runState.Act.
    [JsonPropertyName("boss")] public string? BossEncounterId { get; set; }
    [JsonPropertyName("boss2")] public string? SecondBossEncounterId { get; set; }
}

internal static class MapHistoryIO
{
    private const string Dir = "map_history";
    private const string SidecarSuffix = ".maps.json";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string? SidecarAbsolutePath(long startTime) =>
        SidecarIO.ResolvePath(Dir, startTime + SidecarSuffix);

    internal static bool Exists(long startTime)
    {
        var path = SidecarAbsolutePath(startTime);
        return path != null && File.Exists(path);
    }

    internal static void Write(long startTime, Dictionary<int, CapturedAct> acts)
    {
        var path = SidecarAbsolutePath(startTime);
        if (path == null) return;
        var sidecar = new MapHistorySidecar();
        foreach (var kv in acts)
        {
            sidecar.Acts.Add(new MapHistorySidecarAct
            {
                Index = kv.Key,
                Map = kv.Value.Map,
                Drawings = kv.Value.Drawings,
                VisitedMapCoords = kv.Value.VisitedMapCoords,
                BossEncounterId = kv.Value.BossEncounterId,
                SecondBossEncounterId = kv.Value.SecondBossEncounterId,
            });
        }
        sidecar.Acts.Sort((a, b) => a.Index.CompareTo(b.Index));
        SidecarIO.WriteJson(path, sidecar, _jsonOptions);
    }

    /// <summary>
    /// Writes the current in-memory cache to the sidecar file, merging with
    /// any acts already persisted from prior sessions. Called mid-run after
    /// each act capture so data survives game restarts.
    /// </summary>
    internal static void WriteIncremental(long startTime, IReadOnlyDictionary<int, CapturedAct> cache)
    {
        if (startTime == 0) return;
        var path = SidecarAbsolutePath(startTime);
        if (path == null) return;

        // Read existing sidecar (may have acts from a prior session).
        var existing = SidecarIO.ReadJson<MapHistorySidecar>(path, _jsonOptions);
        var merged = new Dictionary<int, CapturedAct>();

        // Start with any acts already on disk.
        if (existing != null)
        {
            foreach (var act in existing.Acts)
                merged.TryAdd(act.Index, new CapturedAct(act.Map, act.Drawings, act.VisitedMapCoords, act.BossEncounterId, act.SecondBossEncounterId));
        }

        // Overlay current session captures (fresher data wins).
        foreach (var kv in cache)
            merged[kv.Key] = kv.Value;

        Write(startTime, new Dictionary<int, CapturedAct>(merged));
    }

    /// <summary>
    /// Reads existing sidecar acts from disk and adds them to the provided
    /// dictionary (without overwriting entries already present). This recovers
    /// acts captured in prior sessions that are no longer in the in-memory cache.
    /// </summary>
    internal static void MergeFromDisk(long startTime, Dictionary<int, CapturedAct> acts)
    {
        var existing = Read(startTime);
        if (existing == null) return;
        foreach (var act in existing.Acts)
            acts.TryAdd(act.Index, new CapturedAct(act.Map, act.Drawings, act.VisitedMapCoords, act.BossEncounterId, act.SecondBossEncounterId));
    }

    internal static MapHistorySidecar? Read(long startTime)
    {
        var path = SidecarAbsolutePath(startTime);
        return path != null ? SidecarIO.ReadJson<MapHistorySidecar>(path, _jsonOptions) : null;
    }
}
