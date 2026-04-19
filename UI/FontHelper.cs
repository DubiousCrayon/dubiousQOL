using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Assets;

namespace dubiousQOL.UI;

/// <summary>
/// Centralized font loading with simple string identifiers, caching, and fallback.
/// Custom mod fonts map to res://dubiousQOL/fonts/ paths.
/// Game fonts map to res://themes/ assets via PreloadManager.
/// </summary>
internal static class FontHelper
{
    private static readonly Dictionary<string, Font?> _cache = new();

    private static readonly Dictionary<string, string> _identifiers = new()
    {
        // Custom mod fonts (res://dubiousQOL/fonts/)
        ["fightkid"]       = "res://dubiousQOL/fonts/fightkid.ttf",
        ["mighty-souly"]   = "res://dubiousQOL/fonts/Mighty Souly.otf",
        ["beach-flower"]   = "res://dubiousQOL/fonts/BeachFlower-Bold.otf",
        ["kaleo"]          = "res://dubiousQOL/fonts/Kaleo-Regular.ttf",
        ["sanden"]         = "res://dubiousQOL/fonts/SANDEN.ttf",
        ["mgf-firechikns"] = "res://dubiousQOL/fonts/MGF-FirechiknsPersonalUse.otf",

        // Game fonts (res://themes/)
        ["kreon-regular"]  = "res://themes/kreon_regular_shared.tres",
        ["kreon-bold"]     = "res://themes/kreon_bold_shared.tres",
    };

    /// <summary>
    /// Loads a font by identifier. Cached after first load. Returns null on failure.
    /// </summary>
    public static Font? Load(string identifier)
    {
        if (_cache.TryGetValue(identifier, out var cached)
            && (cached == null || GodotObject.IsInstanceValid(cached)))
            return cached;

        if (!_identifiers.TryGetValue(identifier, out var path))
        {
            MainFile.Logger.Warn($"FontHelper: unknown identifier '{identifier}'");
            _cache[identifier] = null;
            return null;
        }

        Font? font = null;
        try
        {
            // Game theme fonts (.tres) go through PreloadManager for proper asset resolution.
            // Custom resource fonts (.ttf/.otf) use ResourceLoader directly.
            if (path.EndsWith(".tres"))
                font = PreloadManager.Cache.GetAsset<Font>(path);
            else
                font = ResourceLoader.Load<Font>(path, null, ResourceLoader.CacheMode.Reuse);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"FontHelper load '{identifier}': {e.Message}");
        }

        _cache[identifier] = font;
        return font;
    }

    /// <summary>
    /// Returns the raw res:// path for a font identifier, for use in BBCode
    /// [font=...] tags or other contexts that need the path string.
    /// Returns null if the identifier is unknown.
    /// </summary>
    public static string? GetPath(string identifier)
    {
        return _identifiers.GetValueOrDefault(identifier);
    }

    private static readonly Dictionary<string, Godot.Theme?> _themeCache = new();

    /// <summary>
    /// Loads a game Theme resource by path. Cached after first load.
    /// </summary>
    public static Godot.Theme? LoadTheme(string path)
    {
        if (_themeCache.TryGetValue(path, out var cached)
            && cached != null && GodotObject.IsInstanceValid(cached))
            return cached;

        Godot.Theme? theme = null;
        try { theme = PreloadManager.Cache.GetAsset<Godot.Theme>(path); }
        catch (Exception e) { MainFile.Logger.Warn($"FontHelper theme load '{path}': {e.Message}"); }

        _themeCache[path] = theme;
        return theme;
    }
}
