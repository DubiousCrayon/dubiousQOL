using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Godot;

namespace dubiousQOL.Config;

public enum ConfigEntryType { Bool, Int, Float, Color, Enum }

public sealed class ConfigEntry
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Description { get; init; }
    public ConfigEntryType Type { get; init; }
    public object DefaultValue { get; init; } = false;
    public object Value { get; set; } = false;
    public float? Min { get; init; }
    public float? Max { get; init; }
    public string[]? EnumOptions { get; init; }

    internal void ResetToDefault() => Value = DefaultValue;
}

public sealed class EntryBuilder
{
    internal readonly List<ConfigEntry> Entries = new();

    public void Bool(string key, string label, bool defaultValue, string? description = null)
    {
        Entries.Add(new ConfigEntry
        {
            Key = key, Label = label, Description = description,
            Type = ConfigEntryType.Bool, DefaultValue = defaultValue, Value = defaultValue,
        });
    }

    public void Int(string key, string label, int defaultValue, int? min = null, int? max = null, string? description = null)
    {
        Entries.Add(new ConfigEntry
        {
            Key = key, Label = label, Description = description,
            Type = ConfigEntryType.Int, DefaultValue = defaultValue, Value = defaultValue,
            Min = min, Max = max,
        });
    }

    public void Float(string key, string label, float defaultValue, float? min = null, float? max = null, string? description = null)
    {
        Entries.Add(new ConfigEntry
        {
            Key = key, Label = label, Description = description,
            Type = ConfigEntryType.Float, DefaultValue = defaultValue, Value = defaultValue,
            Min = min, Max = max,
        });
    }

    public void Color(string key, string label, Color defaultValue, string? description = null)
    {
        Entries.Add(new ConfigEntry
        {
            Key = key, Label = label, Description = description,
            Type = ConfigEntryType.Color, DefaultValue = defaultValue, Value = defaultValue,
        });
    }

    public void Enum(string key, string label, string defaultValue, string[] options, string? description = null)
    {
        Entries.Add(new ConfigEntry
        {
            Key = key, Label = label, Description = description,
            Type = ConfigEntryType.Enum, DefaultValue = defaultValue, Value = defaultValue,
            EnumOptions = options,
        });
    }
}

public abstract class FeatureConfig
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract bool EnabledByDefault { get; }
    public virtual bool RequiresRestart => false;

    public bool Enabled { get; set; }

    private List<ConfigEntry>? _entries;
    public IReadOnlyList<ConfigEntry> Entries => _entries ??= BuildEntries();

    protected abstract void DefineEntries(EntryBuilder builder);

    private List<ConfigEntry> BuildEntries()
    {
        var b = new EntryBuilder();
        DefineEntries(b);
        return b.Entries;
    }

    private string ConfigPath =>
        Path.Combine(OS.GetUserDataDir(), "mod_configs", "dubiousQOL", Id + ".json");

    public void Load()
    {
        Enabled = EnabledByDefault;
        foreach (var entry in Entries) entry.ResetToDefault();

        try
        {
            var path = ConfigPath;
            if (!File.Exists(path))
            {
                Save();
                return;
            }

            var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));
            if (json == null) return;

            if (json.TryGetValue("Enabled", out var enabledEl))
                Enabled = enabledEl.GetBoolean();

            foreach (var entry in Entries)
            {
                if (!json.TryGetValue(entry.Key, out var el)) continue;
                entry.Value = entry.Type switch
                {
                    ConfigEntryType.Bool => el.GetBoolean(),
                    ConfigEntryType.Int => ClampInt(el.GetInt32(), entry),
                    ConfigEntryType.Float => ClampFloat(el.GetSingle(), entry),
                    ConfigEntryType.Color => ParseColor(el.GetString() ?? ""),
                    ConfigEntryType.Enum => ValidateEnum(el.GetString() ?? "", entry),
                    _ => entry.DefaultValue,
                };
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"FeatureConfig.Load [{Id}]: {e.Message}");
        }
    }

    public void Save()
    {
        try
        {
            var dict = new Dictionary<string, object> { ["Enabled"] = Enabled };
            foreach (var entry in Entries)
            {
                dict[entry.Key] = entry.Type == ConfigEntryType.Color
                    ? "#" + ((Color)entry.Value).ToHtml(includeAlpha: false)
                    : entry.Value;
            }

            var path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"FeatureConfig.Save [{Id}]: {e.Message}");
        }
    }

    protected bool GetBool(string key) => (bool)FindEntry(key).Value;
    protected int GetInt(string key) => (int)FindEntry(key).Value;
    protected float GetFloat(string key) => (float)FindEntry(key).Value;
    protected Color GetColor(string key) => (Color)FindEntry(key).Value;
    protected string GetEnum(string key) => (string)FindEntry(key).Value;

    private ConfigEntry FindEntry(string key)
    {
        foreach (var e in Entries)
            if (e.Key == key) return e;
        throw new KeyNotFoundException($"Config entry '{key}' not found in feature '{Id}'.");
    }

    private static int ClampInt(int val, ConfigEntry entry)
    {
        if (entry.Min.HasValue) val = Math.Max(val, (int)entry.Min.Value);
        if (entry.Max.HasValue) val = Math.Min(val, (int)entry.Max.Value);
        return val;
    }

    private static float ClampFloat(float val, ConfigEntry entry)
    {
        if (entry.Min.HasValue) val = Math.Max(val, entry.Min.Value);
        if (entry.Max.HasValue) val = Math.Min(val, entry.Max.Value);
        return val;
    }

    private static Color ParseColor(string hex)
    {
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length == 6 &&
            byte.TryParse(hex[0..2], NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(hex[2..4], NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(hex[4..6], NumberStyles.HexNumber, null, out var b))
            return new Color(r / 255f, g / 255f, b / 255f);
        return Colors.White;
    }

    private static string ValidateEnum(string val, ConfigEntry entry)
    {
        if (entry.EnumOptions != null)
            foreach (var opt in entry.EnumOptions)
                if (opt == val) return val;
        return (string)entry.DefaultValue;
    }
}
