using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.sts2.Core.Nodes.TopBar;

namespace dubiousQOL.Patches;

/// <summary>
/// Shows the player's current win streak as a flame badge to the right of the
/// act name. Hidden when streak == 0. Uses bundled flame icons in
/// dubiousQOL/images/winstreak/; tiers upgrade the icon at higher streaks.
/// </summary>
[HarmonyPatch(typeof(NTopBarBossIcon), "OnActEntered")]
public static class PatchWinStreakDisplay
{
    private const string NodeName = "DubiousWinStreakBadge";

    private static WeakReference<TextureRect>? _animFlame;
    private static Texture2D[]? _animFrames;
    private static bool _animHooked;

    [HarmonyPostfix]
    public static void Postfix(NTopBarBossIcon __instance)
    {
        try { Update(__instance); }
        catch (Exception e) { MainFile.Logger.Warn($"WinStreakDisplay: {e.Message}"); }
    }

    private static void Update(NTopBarBossIcon host)
    {
        long streak = GetCurrentStreak(host);
        var parent = host.GetParent();
        if (parent == null) return;

        var badge = parent.GetNodeOrNull<MarginContainer>(NodeName);
        if (streak < 3)
        {
            badge?.QueueFree();
            return;
        }

        if (badge == null)
        {
            badge = CreateBadge();
            if (badge == null) return;
            parent.AddChild(badge);
            var actName = parent.GetNodeOrNull("DubiousActNameWrapper");
            int idx = actName?.GetIndex() ?? host.GetIndex();
            parent.MoveChild(badge, idx + 1);
        }

        int tier = TierFor(streak);
        var frames = FlameIcons.GetFrames(tier);
        var flame = badge.GetNode<TextureRect>("Flame");
        if (frames != null && frames.Length > 0) flame.Texture = frames[0];

        var label = badge.GetNode<Label>("Flame/Number");
        label.Text = streak.ToString();

        _animFlame = new WeakReference<TextureRect>(flame);
        _animFrames = frames;
        if (!_animHooked && host.IsInsideTree())
        {
            host.GetTree().ProcessFrame += OnProcessFrame;
            _animHooked = true;
        }
    }

    // Cycle through the loaded flame frames each tick to play the GIF animation.
    private static void OnProcessFrame()
    {
        try
        {
            if (_animFlame == null || !_animFlame.TryGetTarget(out var flame)) return;
            if (!GodotObject.IsInstanceValid(flame)) return;
            if (_animFrames == null || _animFrames.Length <= 1) return;

            float t = Time.GetTicksMsec() / 1000f;
            int idx = FlameIcons.FrameIndexAt(t, _animFrames.Length);
            var next = _animFrames[idx];
            if (flame.Texture != next) flame.Texture = next;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"WinStreakDisplay anim: {e.Message}");
        }
    }

    private static long GetCurrentStreak(NTopBarBossIcon host)
    {
        var runState = host._runState;
        if (runState?.Players == null || runState.Players.Count == 0) return 0;
        var character = runState.Players[0].Character;
        if (character == null) return 0;
        var progress = SaveManager.Instance?.Progress;
        if (progress == null) return 0;
        return progress.GetOrCreateCharacterStats(character.Id).CurrentWinStreak;
    }

    // Tier thresholds: 1-2, 3-4, 5-9, 10-14, 15-19, 20-24, 25+
    private static int TierFor(long streak)
    {
        if (streak >= 25) return 6;
        if (streak >= 20) return 5;
        if (streak >= 15) return 4;
        if (streak >= 10) return 3;
        if (streak >= 5)  return 2;
        if (streak >= 3)  return 1;
        return 0;
    }

    private static MarginContainer? CreateBadge()
    {
        var tex = FlameIcons.Get(0);
        if (tex == null) return null;

        var size = new Vector2(56, 60);

        var root = new MarginContainer
        {
            Name = NodeName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        // margin_left: gap between act name and the flame.
        // margin_bottom: extra row space below content; combined with
        // ShrinkCenter alignment, this lifts the flame upward in the row.
        root.AddThemeConstantOverride("margin_left", -4);
        root.AddThemeConstantOverride("margin_bottom", 12);

        var flame = new TextureRect
        {
            Name = "Flame",
            Texture = tex,
            CustomMinimumSize = size,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        root.AddChild(flame);

        var label = new Label
        {
            Name = "Number",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.OffsetTop = 20; // sit in the wider belly, not the tip
        label.OffsetLeft = -1;
        label.OffsetRight = -1;
        var fontRes = ResourceLoader.Load<Font>(
            "res://dubiousQOL/fonts/MGF-FirechiknsPersonalUse.otf", null, ResourceLoader.CacheMode.Reuse);
        if (fontRes != null)
            label.AddThemeFontOverride("font", fontRes);
        label.AddThemeFontSizeOverride("font_size", 21);
        label.AddThemeColorOverride("font_color", new Color(0.55f, 0.08f, 0.02f));
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
        label.AddThemeConstantOverride("outline_size", 5);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.7f));
        label.AddThemeConstantOverride("shadow_offset_x", 1);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeConstantOverride("shadow_outline_size", 2);
        // Child of the flame (not a Container) so anchors/OffsetTop aren't
        // overridden by MarginContainer's layout.
        flame.AddChild(label);
        return root;
    }
}

/// <summary>
/// Loads bundled flame frames from dubiousQOL/images/winstreak/tierN_frames/.
/// Falls back to a static tierN.png when a frame folder isn't present, and to
/// tier 0 when the requested tier has no assets at all.
/// </summary>
internal static class FlameIcons
{
    private const float FrameDurationSec = 0.1f; // GIF was 10fps

    private static readonly Dictionary<int, Texture2D[]?> _framesCache = new();

    public static Texture2D? Get(int tier)
    {
        var frames = GetFrames(tier);
        return frames != null && frames.Length > 0 ? frames[0] : null;
    }

    public static Texture2D[]? GetFrames(int tier)
    {
        if (_framesCache.TryGetValue(tier, out var cached)) return cached;

        // Try animated frames first (tier0_frames/frame_00.png, _01.png, ...).
        var list = new List<Texture2D>();
        for (int i = 0; i < 64; i++)
        {
            var path = $"res://dubiousQOL/images/winstreak/tier{tier}_frames/frame_{i:D2}.png";
            var tex = ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse);
            if (tex == null) break;
            list.Add(tex);
        }

        // Fallback to single static texture.
        if (list.Count == 0)
        {
            var staticPath = $"res://dubiousQOL/images/winstreak/tier{tier}.png";
            var staticTex = ResourceLoader.Load<Texture2D>(staticPath, null, ResourceLoader.CacheMode.Reuse);
            if (staticTex != null) list.Add(staticTex);
        }

        // Fall back to tier 0 if this tier has nothing.
        if (list.Count == 0 && tier > 0)
        {
            _framesCache[tier] = GetFrames(0);
            return _framesCache[tier];
        }

        var result = list.Count > 0 ? list.ToArray() : null;
        _framesCache[tier] = result;
        return result;
    }

    public static int FrameIndexAt(float timeSec, int frameCount)
    {
        if (frameCount <= 1) return 0;
        float loopSec = frameCount * FrameDurationSec;
        float t = timeSec % loopSec;
        if (t < 0) t += loopSec;
        int idx = (int)(t / FrameDurationSec);
        if (idx >= frameCount) idx = frameCount - 1;
        return idx;
    }
}
