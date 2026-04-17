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

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterAct))]
    public static class PatchEnterAct
    {
        [HarmonyPrefix]
        public static void Prefix(int currentActIndex)
        {
            if (!DubiousConfig.MapHistory) return;
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
            if (!DubiousConfig.MapHistory) return;
            try
            {
                // Merge the current act's map from the finalized SerializableRun — the
                // only place the final act's ActMap is preserved is run.Acts[current].SavedMap.
                var finalActs = new Dictionary<int, CapturedAct>(_cache);
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
    private const string SidecarSuffix = ".maps.json";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string? SidecarAbsolutePath(long startTime)
    {
        try
        {
            int profileId = SaveManager.Instance.CurrentProfileId;
            // GetProfileScopedPath yields "user://{platform}/{userId}/{profileDir}/{dataType}".
            // RunHistorySaveManager.GetHistoryPath only returns "{profileDir}/saves/history"
            // (no platform/userId scope) — prepending user:// to that lands in the wrong dir.
            //
            // Sidecars MUST live OUTSIDE the history/ directory. The game's
            // RunHistorySaveManager.LoadAllRunHistoryNames enumerates every file in
            // history/ and filters only .corrupt/.backup — any other file (like our
            // .maps.json) gets fed to LoadRunHistory, fails deserialization, and
            // MigrationManager.PreserveCorruptFile renames it to .corrupt. That's why
            // the sidecar vanishes after revisiting run history. Use a sibling dir.
            string userPath = UserDataPathProvider.GetProfileScopedPath(profileId, UserDataPathProvider.SavesDir);
            string abs = ProjectSettings.GlobalizePath(userPath);
            return Path.Combine(abs, "map_history", startTime + SidecarSuffix);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"MapHistory path resolve: {e.Message}");
            return null;
        }
    }

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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(sidecar, _jsonOptions));
    }

    internal static MapHistorySidecar? Read(long startTime)
    {
        try
        {
            var path = SidecarAbsolutePath(startTime);
            if (path == null || !File.Exists(path)) return null;
            return JsonSerializer.Deserialize<MapHistorySidecar>(File.ReadAllText(path), _jsonOptions);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"MapHistory sidecar read: {e.Message}");
            return null;
        }
    }
}
