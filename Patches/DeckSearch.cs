using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace dubiousQOL.Patches;

/// <summary>
/// Adds a live-filtering search bar to the mid-run deck view (NDeckViewScreen),
/// mirroring the compendium's NSearchBar behavior. Matches against card title +
/// description (upgrade-aware) and supports rarity/type keyword shortcuts.
/// Per-instance state — opening a fresh deck view starts with an empty query.
/// </summary>
internal sealed class DeckSearchState
{
    public string Query = "";
    public NSearchBar? SearchBar;
}

public static class DeckSearchRegistry
{
    internal static readonly ConditionalWeakTable<NDeckViewScreen, DeckSearchState> States = new();

    internal static DeckSearchState GetOrCreate(NDeckViewScreen screen)
    {
        if (!States.TryGetValue(screen, out var state))
        {
            state = new DeckSearchState();
            States.Add(screen, state);
        }
        return state;
    }

    // Special keyword shortcuts — exact-match query triggers a predicate instead of text search.
    private static readonly Dictionary<string, Func<CardModel, bool>> SpecialKeywords = BuildSpecialKeywords();

    private static Dictionary<string, Func<CardModel, bool>> BuildSpecialKeywords()
    {
        var dict = new Dictionary<string, Func<CardModel, bool>>();
        foreach (CardRarity rarity in Enum.GetValues(typeof(CardRarity)))
        {
            var captured = rarity;
            dict[rarity.ToString().ToLowerInvariant()] = c => c.Rarity == captured;
        }
        foreach (CardType type in Enum.GetValues(typeof(CardType)))
        {
            var captured = type;
            var key = type.ToString().ToLowerInvariant();
            // Don't clobber a rarity keyword with a type keyword (e.g. "curse", "status" exist in both).
            // Rarity wins since it's more useful in-deck.
            if (!dict.ContainsKey(key))
                dict[key] = c => c.Type == captured;
        }
        return dict;
    }

    internal static bool TryApplyKeyword(string queryLower, CardModel card, out bool matched)
    {
        if (SpecialKeywords.TryGetValue(queryLower, out var predicate))
        {
            matched = predicate(card);
            return true;
        }
        matched = false;
        return false;
    }

    /// <summary>
    /// Build the search bar by instantiating the card_library scene, plucking
    /// %SearchBar, and freeing the rest. Reuses the compendium's exact visual/input scene.
    /// </summary>
    internal static NSearchBar? InstantiateSearchBar()
    {
        try
        {
            var packed = ResourceLoader.Load<PackedScene>(
                "res://scenes/screens/card_library/card_library.tscn", null, ResourceLoader.CacheMode.Reuse);
            if (packed == null) return null;

            var libRoot = packed.Instantiate<Node>(PackedScene.GenEditState.Disabled);
            var searchBar = FindDescendant<NSearchBar>(libRoot);
            if (searchBar != null)
            {
                searchBar.GetParent().RemoveChild(searchBar);
            }
            libRoot.QueueFree();
            return searchBar;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to extract SearchBar from card_library scene: {e.Message}");
            return null;
        }
    }

    private static T? FindDescendant<T>(Node root) where T : class
    {
        if (root is T match) return match;
        foreach (Node child in root.GetChildren())
        {
            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}

[HarmonyPatch(typeof(NDeckViewScreen), nameof(NDeckViewScreen._Ready))]
public static class PatchDeckViewReady
{
    [HarmonyPostfix]
    public static void Postfix(NDeckViewScreen __instance)
    {
        var state = DeckSearchRegistry.GetOrCreate(__instance);
        if (state.SearchBar != null) return; // idempotent

        var searchBar = DeckSearchRegistry.InstantiateSearchBar();
        if (searchBar == null)
        {
            MainFile.Logger.Warn("DeckSearch: could not instantiate SearchBar, skipping.");
            return;
        }

        state.SearchBar = searchBar;

        // Wire up live filtering. QueryChanged fires as user types (and when clear 'X' is pressed).
        searchBar.QueryChanged += query =>
        {
            state.Query = query ?? "";
            __instance.DisplayCards();
        };

        // Place it centered, sitting above/on the sort bar. We parent to the sort bar's parent
        // so it lives in the same layout space, then anchor to top-center.
        var sortBg = __instance._bg;
        if (sortBg == null)
        {
            MainFile.Logger.Warn("DeckSearch: _bg (SortingBg) not found; cannot position search bar.");
            return;
        }

        var parent = sortBg.GetParent();
        parent.AddChild(searchBar);
        parent.MoveChild(searchBar, sortBg.GetIndex()); // sit just before the sort bg in z-order

        // Anchor: centered horizontally, vertically aligned to top of sort bg.
        searchBar.AnchorLeft = 0.5f;
        searchBar.AnchorRight = 0.5f;
        searchBar.AnchorTop = 0f;
        searchBar.AnchorBottom = 0f;
        searchBar.OffsetLeft = -167f;  // ~2/3 of previous 500-wide bar
        searchBar.OffsetRight = 167f;
        // Bottom edge sits just above the sort bar so the bar's white outline stays visible.
        searchBar.OffsetBottom = sortBg.OffsetTop - 5f;
        searchBar.OffsetTop = searchBar.OffsetBottom - 60f;
        searchBar.MouseFilter = Control.MouseFilterEnum.Pass;
    }
}

[HarmonyPatch(typeof(NDeckViewScreen), "DisplayCards")]
public static class PatchDeckViewDisplayCards
{
    [HarmonyPrefix]
    public static bool Prefix(NDeckViewScreen __instance)
    {
        if (!DeckSearchRegistry.States.TryGetValue(__instance, out var state))
            return true;

        var query = state.Query;
        if (string.IsNullOrWhiteSpace(query))
            return true; // no filter — let original DisplayCards run

        var showUpgrades = GetShowUpgradesTicked(__instance);
        var filtered = FilterCards(__instance._cards, query, showUpgrades).ToList();

        __instance._grid.YOffset = 100;
        __instance._grid.SetCards(filtered, __instance._pile.Type, __instance._sortingPriority);

        var topRow = __instance._grid.GetTopRowOfCardNodes();
        if (topRow != null)
        {
            foreach (var holder in topRow)
                holder.FocusNeighborTop = __instance._obtainedSorter.GetPath();
        }

        return false; // skip original
    }

    private static bool GetShowUpgradesTicked(NDeckViewScreen screen)
    {
        // _showUpgrades is private on the NCardsViewScreen base. Publicize exposes it.
        var tickbox = ((NCardsViewScreen)screen)._showUpgrades;
        return tickbox != null && tickbox.IsTicked;
    }

    private static IEnumerable<CardModel> FilterCards(IEnumerable<CardModel> cards, string query, bool showUpgrades)
    {
        var queryLower = query.ToLowerInvariant().Trim();

        foreach (var card in cards)
        {
            // Special keyword shortcut (exact-match query like "common", "attack").
            if (DeckSearchRegistry.TryApplyKeyword(queryLower, card, out var keywordMatched))
            {
                if (keywordMatched) yield return card;
                continue;
            }

            // Text match on title + description.
            // Upgrade-aware: match what the card visually displays. GetDescriptionForPile
            // auto-uses upgraded text when IsUpgraded. When the %Upgrades toggle is on and
            // the card isn't already upgraded, clone + upgrade the clone (mirroring what
            // NGridCardHolder does) so keyword-changes on upgrade (e.g. Chill losing Exhaust)
            // are reflected in search.
            CardModel displayCard = card;
            if (showUpgrades && !card.IsUpgraded && card.IsUpgradable)
            {
                try
                {
                    var clone = (CardModel)card.MutableClone();
                    clone.UpgradeInternal();
                    displayCard = clone;
                }
                catch (Exception e)
                {
                    MainFile.Logger.Warn($"DeckSearch: upgrade-preview clone failed for {card.Id}: {e.Message}");
                }
            }

            string description = displayCard.GetDescriptionForPile(PileType.None);
            var combined = displayCard.Title + " " + description;
            var normalized = NSearchBar.Normalize(NSearchBar.RemoveHtmlTags(combined.StripBbCode()));

            if (normalized.Contains(queryLower))
                yield return card;
        }
    }
}

/// <summary>
/// The %Upgrades tickbox changes what description text is searchable. When the user
/// toggles it while a query is active, we need to re-filter so results stay accurate.
/// </summary>
[HarmonyPatch(typeof(NCardsViewScreen), "ToggleShowUpgrades")]
public static class PatchToggleShowUpgrades
{
    [HarmonyPostfix]
    public static void Postfix(NCardsViewScreen __instance)
    {
        if (__instance is NDeckViewScreen deckView
            && DeckSearchRegistry.States.TryGetValue(deckView, out var state)
            && !string.IsNullOrWhiteSpace(state.Query))
        {
            deckView.DisplayCards();
        }
    }
}
