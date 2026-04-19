using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rooms;

namespace dubiousQOL.UI;

internal enum SourceKind
{
    Attack, AoeAttack, BlockSkill, Skill, Power,
    Status, Curse, Relic, Potion, Orb,
    Creature, Debuff, Character, Other
}

internal struct ResolvedSource
{
    public Texture2D? Icon;
    public SourceKind Kind;
    public Color BarColor;
    // Atlas sprites and category icons have built-in transparent padding.
    // Scale > 1 renders the TextureRect larger than the clip container so
    // the padding overflows and the visible content fills the box.
    public float IconScale;
}

/// <summary>
/// Resolves a source name string (from StatsTrackerData breakdown dictionaries)
/// to an icon texture and semantic bar color at display time. Results are cached
/// per source name and cleared on combat/act/run reset.
///
/// Resolution order: hardcoded orbs → hardcoded debuffs → cards (by title) →
/// relics → potions → monsters → characters → fallback gray.
/// </summary>
internal static class SourceIconResolver
{
    private static readonly Dictionary<string, ResolvedSource> _cache = new();

    // Category icon textures, lazy-loaded once.
    private static Texture2D? _attackIcon;
    private static Texture2D? _aoeIcon;
    private static Texture2D? _defendIcon;
    private static Texture2D? _skillIcon;
    private static Texture2D? _powerIcon;
    private static Texture2D? _monsterIcon;
    private static Texture2D? _eliteIcon;
    private static Texture2D? _minionIcon;

    // Monster → encounter room type, built once from all acts' encounters.
    // Monsters not present in any encounter are minions (spawned mid-combat).
    private static Dictionary<string, RoomType>? _monsterRoomTypes;
    private static Dictionary<string, string>? _bossEncounterSlugs; // monster ID → encounter slug
    private static HashSet<string>? _allEncounterMonsters;

    // Semantic colors per source kind.
    private static readonly Color ColorAttack      = new(0.85f, 0.35f, 0.35f);
    private static readonly Color ColorAoeAttack   = new(0.90f, 0.50f, 0.30f);
    private static readonly Color ColorBlockSkill  = new(0.35f, 0.65f, 0.90f);
    private static readonly Color ColorSkill       = new(0.30f, 0.80f, 0.75f);
    private static readonly Color ColorPower       = new(0.90f, 0.70f, 0.30f);
    private static readonly Color ColorStatus      = new(0.85f, 0.55f, 0.30f);
    private static readonly Color ColorCurse       = new(0.70f, 0.30f, 0.70f);
    private static readonly Color ColorRelic       = new(0.85f, 0.75f, 0.40f);
    private static readonly Color ColorPotion      = new(0.45f, 0.80f, 0.45f);
    private static readonly Color ColorCreature    = new(0.75f, 0.25f, 0.25f);
    private static readonly Color ColorCharacter   = new(0.55f, 0.55f, 0.60f);
    private static readonly Color ColorOther       = new(0.50f, 0.50f, 0.55f);

    // Atlas sprites and category icons have transparent padding that makes
    // them look tiny at 1:1. This scale renders the TextureRect larger than
    // the clip container so the padding overflows and gets cropped.
    private const float PaddedIconScale = 1.6f;
    // run_history PNGs (monster/elite) have less padding than atlas sprites.
    private const float RunHistoryIconScale = 1.25f;

    // Per-orb colors.
    private static readonly Dictionary<string, Color> OrbColors = new()
    {
        ["Lightning Orb"] = new Color(0.90f, 0.80f, 0.20f),
        ["Frost Orb"]     = new Color(0.40f, 0.70f, 0.95f),
        ["Dark Orb"]      = new Color(0.55f, 0.20f, 0.80f),
        ["Glass Orb"]     = new Color(0.20f, 0.75f, 0.75f),
    };

    // Per-debuff colors.
    private static readonly Dictionary<string, (Type powerType, Color color)> DebuffMap = new()
    {
        ["Poison"]   = (typeof(PoisonPower),   new Color(0.50f, 0.80f, 0.30f)),
        ["Doom"]     = (typeof(DoomPower),     new Color(0.45f, 0.20f, 0.65f)),
        ["Haunt"]    = (typeof(HauntPower),    new Color(0.40f, 0.55f, 0.80f)),
        ["Strangle"] = (typeof(StranglePower), new Color(0.70f, 0.25f, 0.25f)),
    };

    // Orb type map for icon lookup.
    private static readonly Dictionary<string, Type> OrbTypes = new()
    {
        ["Lightning Orb"] = typeof(LightningOrb),
        ["Frost Orb"]     = typeof(FrostOrb),
        ["Dark Orb"]      = typeof(DarkOrb),
        ["Glass Orb"]     = typeof(GlassOrb),
    };

    public static ResolvedSource Resolve(string sourceName)
    {
        if (_cache.TryGetValue(sourceName, out var cached))
            return cached;

        var result = TryOrb(sourceName)
                  ?? TryDebuff(sourceName)
                  ?? TryCard(sourceName)
                  ?? TryRelic(sourceName)
                  ?? TryPotion(sourceName)
                  ?? TryMonster(sourceName)
                  ?? TryCharacter(sourceName)
                  ?? new ResolvedSource { Icon = null, Kind = SourceKind.Other, BarColor = ColorOther, IconScale = 1f };

        _cache[sourceName] = result;
        return result;
    }

    public static void ClearCache() => _cache.Clear();

    private static ResolvedSource? TryOrb(string name)
    {
        if (!OrbTypes.TryGetValue(name, out var orbType)) return null;
        if (!OrbColors.TryGetValue(name, out var color)) color = ColorOther;

        Texture2D? icon = null;
        try
        {
            var orb = ModelDb.GetById<OrbModel>(ModelDb.GetId(orbType));
            icon = orb?.Icon;
        }
        catch { }

        return new ResolvedSource { Icon = icon, Kind = SourceKind.Orb, BarColor = color, IconScale = 1f };
    }

    private static ResolvedSource? TryDebuff(string name)
    {
        if (!DebuffMap.TryGetValue(name, out var entry)) return null;

        Texture2D? icon = null;
        try
        {
            var power = ModelDb.GetById<PowerModel>(ModelDb.GetId(entry.powerType));
            icon = power?.Icon;
        }
        catch { }

        return new ResolvedSource { Icon = icon, Kind = SourceKind.Debuff, BarColor = entry.color, IconScale = PaddedIconScale };
    }

    private static ResolvedSource? TryCard(string name)
    {
        // Card titles from StatsTrackerData include "+" or "+N" for upgraded cards.
        // ModelDb canonical cards are unupgraded, so strip the suffix for matching.
        string baseName = StripUpgradeSuffix(name);

        foreach (var card in ModelDb.AllCards)
        {
            string cardTitle;
            try { cardTitle = card.TitleLocString.GetFormattedText(); }
            catch { continue; }

            if (!string.Equals(cardTitle, baseName, StringComparison.Ordinal)) continue;

            return card.Type switch
            {
                CardType.Attack => new ResolvedSource
                {
                    Icon = IsAoe(card) ? LoadAoeIcon() : LoadAttackIcon(),
                    Kind = IsAoe(card) ? SourceKind.AoeAttack : SourceKind.Attack,
                    BarColor = IsAoe(card) ? ColorAoeAttack : ColorAttack,
                    IconScale = PaddedIconScale,
                },
                CardType.Skill => new ResolvedSource
                {
                    Icon = card.GainsBlock ? LoadDefendIcon() : LoadSkillIcon(),
                    Kind = card.GainsBlock ? SourceKind.BlockSkill : SourceKind.Skill,
                    BarColor = card.GainsBlock ? ColorBlockSkill : ColorSkill,
                    IconScale = PaddedIconScale,
                },
                CardType.Power => new ResolvedSource
                {
                    Icon = LoadPowerIcon(),
                    Kind = SourceKind.Power,
                    BarColor = ColorPower,
                    IconScale = PaddedIconScale,
                },
                CardType.Status => new ResolvedSource
                {
                    Icon = SafeLoadTexture(card.PortraitPath),
                    Kind = SourceKind.Status,
                    BarColor = ColorStatus,
                    IconScale = 1f,
                },
                CardType.Curse => new ResolvedSource
                {
                    Icon = SafeLoadTexture(card.PortraitPath),
                    Kind = SourceKind.Curse,
                    BarColor = ColorCurse,
                    IconScale = 1f,
                },
                _ => null,
            };
        }
        return null;
    }

    private static ResolvedSource? TryRelic(string name)
    {
        foreach (var relic in ModelDb.AllRelics)
        {
            string title;
            try { title = relic.Title.GetFormattedText(); }
            catch { continue; }

            if (!string.Equals(title, name, StringComparison.Ordinal)) continue;
            Texture2D? icon = null;
            try { icon = relic.Icon; } catch { }
            return new ResolvedSource { Icon = icon, Kind = SourceKind.Relic, BarColor = ColorRelic, IconScale = 1f };
        }
        return null;
    }

    private static ResolvedSource? TryPotion(string name)
    {
        foreach (var potion in ModelDb.AllPotions)
        {
            string title;
            try { title = potion.Title.GetFormattedText(); }
            catch { continue; }

            if (!string.Equals(title, name, StringComparison.Ordinal)) continue;
            Texture2D? icon = null;
            try { icon = potion.Image; } catch { }
            return new ResolvedSource { Icon = icon, Kind = SourceKind.Potion, BarColor = ColorPotion, IconScale = 1f };
        }
        return null;
    }

    private static ResolvedSource? TryMonster(string name)
    {
        foreach (var monster in ModelDb.Monsters)
        {
            string title;
            try { title = monster.Title.GetFormattedText(); }
            catch { continue; }

            if (!string.Equals(title, name, StringComparison.Ordinal)) continue;

            Texture2D? icon;
            float scale;
            if (IsMinion(monster))
            {
                icon = LoadMinionIcon();
                scale = PaddedIconScale;
            }
            else
            {
                var roomType = GetMonsterRoomType(monster);
                if (roomType == RoomType.Boss)
                {
                    string slug = _bossEncounterSlugs?.GetValueOrDefault(monster.Id.Entry)
                               ?? monster.Id.Entry.ToLowerInvariant();
                    string path = $"res://images/ui/run_history/{slug}.png";
                    icon = ResourceLoader.Exists(path)
                        ? SafeLoadTexture(path)
                        : LoadMonsterIcon();
                    scale = 1f;
                }
                else if (roomType == RoomType.Elite)
                {
                    icon = LoadEliteIcon();
                    scale = RunHistoryIconScale;
                }
                else
                {
                    icon = LoadMonsterIcon();
                    scale = RunHistoryIconScale;
                }
            }

            return new ResolvedSource { Icon = icon, Kind = SourceKind.Creature, BarColor = ColorCreature, IconScale = scale };
        }
        return null;
    }

    // Builds a lookup of monster ID → encounter RoomType from all acts.
    // Boss/Elite entries take priority over Monster via TryAdd ordering.
    // Also populates _allEncounterMonsters so we can detect minions (monsters
    // that never appear in any encounter and are spawned mid-combat).
    private static RoomType GetMonsterRoomType(MonsterModel monster)
    {
        if (_monsterRoomTypes == null)
        {
            _monsterRoomTypes = new Dictionary<string, RoomType>();
            _bossEncounterSlugs = new Dictionary<string, string>();
            _allEncounterMonsters = new HashSet<string>();
            try
            {
                foreach (var act in ModelDb.Acts)
                {
                    foreach (var enc in act.AllBossEncounters)
                        foreach (var m in enc.AllPossibleMonsters)
                        {
                            _monsterRoomTypes.TryAdd(m.Id.Entry, RoomType.Boss);
                            // Run history icons use the encounter ID, not the monster ID.
                            _bossEncounterSlugs.TryAdd(m.Id.Entry, enc.Id.Entry.ToLowerInvariant());
                            _allEncounterMonsters.Add(m.Id.Entry);
                        }
                    foreach (var enc in act.AllEliteEncounters)
                        foreach (var m in enc.AllPossibleMonsters)
                        {
                            _monsterRoomTypes.TryAdd(m.Id.Entry, RoomType.Elite);
                            _allEncounterMonsters.Add(m.Id.Entry);
                        }
                    foreach (var enc in act.AllWeakEncounters)
                        foreach (var m in enc.AllPossibleMonsters)
                            _allEncounterMonsters.Add(m.Id.Entry);
                    foreach (var enc in act.AllRegularEncounters)
                        foreach (var m in enc.AllPossibleMonsters)
                            _allEncounterMonsters.Add(m.Id.Entry);
                }
            }
            catch { }
        }
        return _monsterRoomTypes.GetValueOrDefault(monster.Id.Entry, RoomType.Monster);
    }

    private static bool IsMinion(MonsterModel monster)
    {
        // Ensure encounter data is built.
        GetMonsterRoomType(monster);
        return _allEncounterMonsters != null && !_allEncounterMonsters.Contains(monster.Id.Entry);
    }

    private static ResolvedSource? TryCharacter(string name)
    {
        foreach (var character in ModelDb.AllCharacters)
        {
            string title;
            try { title = character.Title.GetFormattedText(); }
            catch { continue; }

            if (!string.Equals(title, name, StringComparison.Ordinal)) continue;
            Texture2D? icon = null;
            try { icon = character.IconTexture; } catch { }
            return new ResolvedSource { Icon = icon, Kind = SourceKind.Character, BarColor = ColorCharacter, IconScale = 1f };
        }
        return null;
    }

    // Strips trailing "+" or "+N" from upgraded card titles so we can match
    // against canonical (unupgraded) ModelDb entries.
    private static string StripUpgradeSuffix(string name)
    {
        if (name.Length == 0) return name;
        // "+N" suffix (multi-level upgrades): "Strike+2" → "Strike"
        int plusIdx = name.LastIndexOf('+');
        if (plusIdx > 0)
            return name[..plusIdx];
        return name;
    }

    private static bool IsAoe(CardModel card) =>
        card.TargetType != TargetType.AnyEnemy;

    // Category icon loaders — loaded once, then cached in static fields.
    private static Texture2D? LoadAttackIcon() =>
        _attackIcon ??= SafeLoadTexture("res://images/atlases/intent_atlas.sprites/attack/intent_attack_3.tres");

    private static Texture2D? LoadAoeIcon()
    {
        if (_aoeIcon != null) return _aoeIcon;
        try { _aoeIcon = ModelDb.Power<CrushUnderPower>().Icon; }
        catch { _aoeIcon = LoadAttackIcon(); }
        return _aoeIcon;
    }

    private static Texture2D? LoadDefendIcon() =>
        _defendIcon ??= SafeLoadTexture("res://images/atlases/intent_atlas.sprites/intent_defend.tres");

    private static Texture2D? LoadSkillIcon() =>
        _skillIcon ??= SafeLoadTexture("res://images/packed/run_history/skill_portrait.png");

    private static Texture2D? LoadPowerIcon() =>
        _powerIcon ??= SafeLoadTexture("res://images/packed/run_history/power_portrait.png");

    private static Texture2D? LoadMonsterIcon() =>
        _monsterIcon ??= SafeLoadTexture("res://images/ui/run_history/monster.png");

    private static Texture2D? LoadEliteIcon() =>
        _eliteIcon ??= SafeLoadTexture("res://images/ui/run_history/elite.png");

    private static Texture2D? LoadMinionIcon() =>
        _minionIcon ??= SafeLoadTexture("res://images/atlases/power_atlas.sprites/minionpower.tres");

    private static Texture2D? SafeLoadTexture(string path)
    {
        try { return ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse); }
        catch { return null; }
    }
}
