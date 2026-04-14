using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace dubiousQOL;

/// <summary>
/// Per-feature on/off toggles. Loaded from a JSON file on startup; patches early-return
/// when their flag is off. Edit the file under the game's user_data/mod_configs/ folder
/// and restart to apply — no in-game UI yet.
/// </summary>
public static class DubiousConfig
{
    public static bool ActNameDisplay = true;
    public static bool WinStreakDisplay = true;
    public static bool DeckSearch = true;
    public static bool RarityDisplay = true;
    public static bool UnifiedSavePath = true;
    public static bool SkipSplash = true;
    public static bool IncomingDamageDisplay = true;

    private static string ConfigPath => Path.Combine(OS.GetUserDataDir(), "mod_configs", "dubiousQOL.cfg");

    public static void Load()
    {
        try
        {
            var path = ConfigPath;
            if (!File.Exists(path))
            {
                Save(); // seed a file with defaults so the user can find and edit it
                return;
            }
            var dict = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path));
            if (dict == null) return;
            if (dict.TryGetValue(nameof(ActNameDisplay), out var a)) ActNameDisplay = a;
            if (dict.TryGetValue(nameof(WinStreakDisplay), out var w)) WinStreakDisplay = w;
            if (dict.TryGetValue(nameof(DeckSearch), out var d)) DeckSearch = d;
            if (dict.TryGetValue(nameof(RarityDisplay), out var r)) RarityDisplay = r;
            if (dict.TryGetValue(nameof(UnifiedSavePath), out var u)) UnifiedSavePath = u;
            if (dict.TryGetValue(nameof(SkipSplash), out var s)) SkipSplash = s;
            if (dict.TryGetValue(nameof(IncomingDamageDisplay), out var i)) IncomingDamageDisplay = i;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"DubiousConfig.Load failed: {e.Message}");
        }
    }

    public static void Save()
    {
        try
        {
            var path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var dict = new Dictionary<string, bool>
            {
                [nameof(ActNameDisplay)] = ActNameDisplay,
                [nameof(WinStreakDisplay)] = WinStreakDisplay,
                [nameof(DeckSearch)] = DeckSearch,
                [nameof(RarityDisplay)] = RarityDisplay,
                [nameof(UnifiedSavePath)] = UnifiedSavePath,
                [nameof(SkipSplash)] = SkipSplash,
                [nameof(IncomingDamageDisplay)] = IncomingDamageDisplay,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"DubiousConfig.Save failed: {e.Message}");
        }
    }
}
