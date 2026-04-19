using System.Collections.Generic;
using Godot;

namespace dubiousQOL.UI;

/// <summary>
/// Loads sequentially-numbered PNG frames from a directory for sprite
/// animation. Caches loaded frame arrays by path pattern. Provides frame
/// index calculation for time-based playback.
/// </summary>
internal static class SpriteFrameLoader
{
    private static readonly Dictionary<string, Texture2D[]?> _cache = new();

    /// <summary>
    /// Loads numbered frames matching the pattern (e.g. "res://mod/frames/frame_{0:D2}.png").
    /// Stops at the first missing index. Falls back to fallbackPath if no frames found.
    /// Returns null if nothing loads.
    /// </summary>
    public static Texture2D[]? LoadFrames(string pathFormat, int maxFrames = 64,
        string? fallbackPath = null)
    {
        if (_cache.TryGetValue(pathFormat, out var cached))
            return cached;

        var list = new List<Texture2D>();
        for (int i = 0; i < maxFrames; i++)
        {
            var path = string.Format(pathFormat, i);
            var tex = ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse);
            if (tex == null) break;
            list.Add(tex);
        }

        if (list.Count == 0 && fallbackPath != null)
        {
            var staticTex = ResourceLoader.Load<Texture2D>(
                fallbackPath, null, ResourceLoader.CacheMode.Reuse);
            if (staticTex != null) list.Add(staticTex);
        }

        var result = list.Count > 0 ? list.ToArray() : null;
        _cache[pathFormat] = result;
        return result;
    }

    /// <summary>
    /// Returns the frame index for a given time, looping at the natural cycle duration.
    /// </summary>
    public static int FrameIndexAt(float timeSec, int frameCount, float frameDurationSec)
    {
        if (frameCount <= 1) return 0;
        float loopSec = frameCount * frameDurationSec;
        float t = timeSec % loopSec;
        if (t < 0) t += loopSec;
        int idx = (int)(t / frameDurationSec);
        if (idx >= frameCount) idx = frameCount - 1;
        return idx;
    }
}
