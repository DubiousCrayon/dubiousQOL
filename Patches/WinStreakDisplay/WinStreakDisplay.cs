using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.sts2.Core.Nodes.TopBar;

using dubiousQOL.UI;

namespace dubiousQOL.Patches;

/// <summary>
/// Shows the player's current win streak as a flame badge to the right of the
/// act name. Hidden when streak &lt; 3. Uses bundled flame frames in
/// dubiousQOL/images/winstreak/winstreak_frames/.
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
        if (!WinStreakDisplayConfig.Instance.Enabled) return;
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

        var frames = FlameIcons.GetFrames();
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

    private static MarginContainer? CreateBadge()
    {
        var tex = FlameIcons.Get();
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
        root.AddThemeConstantOverride("margin_left", 15);
        root.AddThemeConstantOverride("margin_bottom", 0);

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
        label.OffsetTop = 42; // sit in the wider belly, not the tip
        label.OffsetRight = 2;
        var fontRes = FontHelper.Load("mgf-firechikns");
        if (fontRes != null)
            label.AddThemeFontOverride("font", fontRes);
        label.AddThemeFontSizeOverride("font_size", 16);
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

        var arch = CreateArchLabel("WINSTREAK",
            radius: 34f,
            arcDegrees: 135f,
            centerOffsetY: 6f,
            fontSize: 11);
        flame.AddChild(arch);
        return root;
    }

    // Builds a Control whose child Labels sit on a circular arc at the top of
    // the parent. centerOffsetY nudges the arc's center (useful when the flame
    // circle isn't centered in its bounding box). Each glyph is rotated so its
    // baseline is tangent to the arc.
    private static Control CreateArchLabel(string text, float radius, float arcDegrees, float centerOffsetY, int fontSize)
    {
        var container = new Control
        {
            Name = "Arch",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = 0,
            OffsetRight = 0,
            OffsetTop = centerOffsetY,
            OffsetBottom = centerOffsetY,
        };

        var font = FontHelper.Load("fightkid");

        int n = text.Length;
        if (n == 0) return container;

        float arcRad = Mathf.DegToRad(arcDegrees);
        float step = n > 1 ? arcRad / (n - 1) : 0;
        float startAngle = -Mathf.Pi / 2f - arcRad / 2f;

        var glyphSize = new Vector2(fontSize + 6, fontSize + 8);

        for (int i = 0; i < n; i++)
        {
            float angle = startAngle + step * i;
            float x = Mathf.Cos(angle) * radius;
            float y = Mathf.Sin(angle) * radius;

            var ch = new Label
            {
                Text = text[i].ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Size = glyphSize,
                PivotOffset = glyphSize / 2f,
                Position = new Vector2(x, y) - glyphSize / 2f,
                Rotation = angle + Mathf.Pi / 2f,
            };
            if (font != null) ch.AddThemeFontOverride("font", font);
            ch.AddThemeFontSizeOverride("font_size", fontSize);
            ch.AddThemeColorOverride("font_color", new Color(1f, 0.78f, 0.22f));
            ch.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
            ch.AddThemeConstantOverride("outline_size", 6);
            ch.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f));
            ch.AddThemeConstantOverride("shadow_offset_x", 0);
            ch.AddThemeConstantOverride("shadow_offset_y", 2);
            ch.AddThemeConstantOverride("shadow_outline_size", 3);
            container.AddChild(ch);
        }
        return container;
    }
}

/// <summary>
/// Loads bundled flame frames from dubiousQOL/images/winstreak/winstreak_frames/.
/// Falls back to the static winstreak.png if the frame folder is empty.
/// </summary>
internal static class FlameIcons
{
    private const float FrameDurationSec = 1f / 30f;

    private static Texture2D[]? _framesCache;
    private static bool _loaded;

    public static Texture2D? Get()
    {
        var frames = GetFrames();
        return frames != null && frames.Length > 0 ? frames[0] : null;
    }

    public static Texture2D[]? GetFrames()
    {
        if (_loaded) return _framesCache;

        var list = new List<Texture2D>();
        for (int i = 0; i < 64; i++)
        {
            var path = $"res://dubiousQOL/images/winstreak/winstreak_frames/frame_{i:D2}.png";
            var tex = ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse);
            if (tex == null) break;
            list.Add(tex);
        }

        if (list.Count == 0)
        {
            var staticTex = ResourceLoader.Load<Texture2D>(
                "res://dubiousQOL/images/winstreak/winstreak.png", null, ResourceLoader.CacheMode.Reuse);
            if (staticTex != null) list.Add(staticTex);
        }

        _framesCache = list.Count > 0 ? list.ToArray() : null;
        _loaded = true;
        return _framesCache;
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
