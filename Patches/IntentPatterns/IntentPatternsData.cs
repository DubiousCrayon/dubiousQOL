using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace dubiousQOL.Patches;

// --- JSON deserialization model ---

internal sealed class IntentPatternsFile
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("creatures")] public Dictionary<string, CreaturePatternData>? Creatures { get; set; }
}

internal sealed class CreaturePatternData
{
    [JsonPropertyName("moves")] public List<MoveData> Moves { get; set; } = new();
}

internal sealed class MoveData
{
    [JsonPropertyName("stateId")] public string StateId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("effect")] public string Effect { get; set; } = "";
}

// --- Runtime-resolved model ---

internal sealed class ResolvedPattern
{
    public string Name { get; set; } = "";
    public string EffectBBCode { get; set; } = "";
    public List<IntentIcon> Intents { get; set; } = new();
}

internal sealed class IntentIcon
{
    public Texture2D? Texture { get; set; }
    public string Label { get; set; } = "";
}

// --- Effect token definitions ---

internal sealed class EffectToken
{
    public string? IconPath { get; }
    public string DisplayName { get; }
    public string Color { get; }

    public EffectToken(string displayName, string color, string? iconPath = null)
    {
        IconPath = iconPath;
        DisplayName = displayName;
        Color = color;
    }

    public static EffectToken Power(string powerEntry, string displayName, string color)
        => new(displayName, color, $"res://images/powers/{powerEntry}.png");

    public static EffectToken Intent(string intentSprite, string displayName, string color)
        => new(displayName, color, $"res://images/packed/intents/{intentSprite}.png");

    public string ToBBCode(int iconSize)
    {
        string icon = IconPath != null
            ? $"[img={iconSize}x{iconSize}]{IconPath}[/img] "
            : "";
        return $"{icon}[color={Color}]{DisplayName}[/color]";
    }
}

// --- Loader + resolver ---

internal static class IntentPatternsData
{
    private const string DataPath = "res://dubiousQOL/data/intent_patterns.json";
    private const int TokenIconSize = 18;

    private static readonly Dictionary<string, EffectToken> _tokens = new()
    {
        ["THORNS"]   = EffectToken.Power("thorns_power",   "Thorns",   "#90EE90"),
        ["RITUAL"]   = EffectToken.Power("ritual_power",   "Ritual",   "#90EE90"),
        ["STRENGTH"] = EffectToken.Power("strength_power", "Strength", "#90EE90"),
        ["WEAK"]     = EffectToken.Power("weak_power",     "Weak",     "#CC6666"),
        ["FRAIL"]    = EffectToken.Power("frail_power",    "Frail",    "#CC6666"),
        ["BLOCK"]    = EffectToken.Intent("intent_defend",  "Block",   "#6699CC"),
    };

    private static IntentPatternsFile? _enrichment;
    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            using var file = Godot.FileAccess.Open(DataPath, Godot.FileAccess.ModeFlags.Read);
            if (file == null)
            {
                MainFile.Logger.Warn($"IntentPatterns: could not open {DataPath}");
                return;
            }
            string json = file.GetAsText();
            _enrichment = JsonSerializer.Deserialize<IntentPatternsFile>(json);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"IntentPatterns load: {e.Message}");
        }
    }

    public static List<ResolvedPattern> Resolve(NCreature nCreature)
    {
        var creature = nCreature.Entity;
        if (creature == null) return new();

        var monster = creature.Monster;
        if (monster?.MoveStateMachine == null) return new();

        var allMoves = monster.MoveStateMachine.States.Values
            .OfType<MoveState>()
            .ToDictionary(m => m.StateId);

        if (allMoves.Count == 0) return new();

        // Targets for damage calc — use player creatures from combat state.
        var targets = GetTargets(creature);

        var creatureKey = monster.Id.Entry;
        if (_enrichment?.Creatures != null
            && _enrichment.Creatures.TryGetValue(creatureKey, out var enrichment))
        {
            return ResolveEnriched(enrichment, allMoves, creature, targets);
        }

        return ResolveFallback(allMoves.Values, creature, targets);
    }

    private static Creature[] GetTargets(Creature enemy)
    {
        try
        {
            var players = enemy.CombatState?.GetCreaturesOnSide(CombatSide.Player);
            if (players != null)
            {
                var first = players.FirstOrDefault();
                if (first != null) return new[] { first };
            }
        }
        catch { /* combat state unavailable */ }
        return Array.Empty<Creature>();
    }

    private static List<ResolvedPattern> ResolveEnriched(
        CreaturePatternData enrichment,
        Dictionary<string, MoveState> allMoves,
        Creature owner,
        Creature[] targets)
    {
        var results = new List<ResolvedPattern>();

        foreach (var moveData in enrichment.Moves)
        {
            var pattern = new ResolvedPattern { Name = moveData.Name };

            if (allMoves.TryGetValue(moveData.StateId, out var moveState))
            {
                pattern.Intents = ExtractIntentIcons(moveState, owner, targets);
                pattern.EffectBBCode = ResolveTemplate(moveData.Effect, moveState, owner, targets);
            }
            else
            {
                pattern.EffectBBCode = ExpandTokens(moveData.Effect);
            }

            results.Add(pattern);
        }

        return results;
    }

    private static List<ResolvedPattern> ResolveFallback(
        IEnumerable<MoveState> moves,
        Creature owner,
        Creature[] targets)
    {
        var results = new List<ResolvedPattern>();

        foreach (var moveState in moves.OrderBy(m => m.StateId))
        {
            var pattern = new ResolvedPattern
            {
                Name = CleanStateId(moveState.StateId),
                Intents = ExtractIntentIcons(moveState, owner, targets),
                EffectBBCode = BuildFallbackEffect(moveState, owner, targets),
            };
            results.Add(pattern);
        }

        return results;
    }

    private static List<IntentIcon> ExtractIntentIcons(MoveState moveState, Creature owner, Creature[] targets)
    {
        var icons = new List<IntentIcon>();
        foreach (var intent in moveState.Intents)
        {
            try
            {
                var icon = new IntentIcon
                {
                    Texture = intent.GetTexture(targets, owner),
                    Label = intent.GetIntentLabel(targets, owner).GetFormattedText() ?? "",
                };
                icons.Add(icon);
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"IntentPatterns icon extract: {e.Message}");
            }
        }
        return icons;
    }

    private static string ExpandTokens(string text)
    {
        foreach (var (key, token) in _tokens)
        {
            string placeholder = $"{{{key}}}";
            if (text.Contains(placeholder))
                text = text.Replace(placeholder, token.ToBBCode(TokenIconSize));
        }
        return text;
    }

    private static string ResolveTemplate(string template, MoveState moveState, Creature owner, Creature[] targets)
    {
        AttackIntent? attack = null;
        foreach (var intent in moveState.Intents)
        {
            if (intent is AttackIntent a) { attack = a; break; }
        }

        string damage = attack != null ? attack.GetSingleDamage(targets, owner).ToString() : "?";
        int hits = attack?.Repeats ?? 1;
        string hitsSuffix = hits > 1 ? $" x{hits}" : "";

        string result = template
            .Replace("{damage}", damage)
            .Replace("{hits}", hits.ToString())
            .Replace("{hits_suffix}", hitsSuffix);

        return ExpandTokens(result);
    }

    private static string BuildFallbackEffect(MoveState moveState, Creature owner, Creature[] targets)
    {
        var parts = new List<string>();
        foreach (var intent in moveState.Intents)
        {
            try
            {
                var label = intent.GetIntentLabel(targets, owner).GetFormattedText();
                if (!string.IsNullOrEmpty(label))
                    parts.Add(label);
                else
                    parts.Add(intent.IntentType.ToString());
            }
            catch
            {
                parts.Add(intent.IntentType.ToString());
            }
        }
        return string.Join(", ", parts);
    }

    private static string CleanStateId(string stateId)
    {
        var cleaned = stateId;
        if (cleaned.EndsWith("_MOVE", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[..^5];

        var parts = cleaned.Split('_');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                parts[i] = char.ToUpper(parts[i][0]) + parts[i][1..].ToLower();
        }
        return string.Join(" ", parts);
    }
}
