using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.sts2.Core.Nodes.TopBar;

using dubiousQOL.UI;
using dubiousQOL.UI.Custom;

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
    private const float FrameDurationSec = 1f / 30f;
    private const string FramePathFormat = "res://dubiousQOL/images/winstreak/winstreak_frames/frame_{0:D2}.png";
    private const string FallbackPath = "res://dubiousQOL/images/winstreak/winstreak.png";

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

        var frames = SpriteFrameLoader.LoadFrames(FramePathFormat, fallbackPath: FallbackPath);
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

    private static void OnProcessFrame()
    {
        try
        {
            if (_animFlame == null || !_animFlame.TryGetTarget(out var flame)) return;
            if (!GodotObject.IsInstanceValid(flame)) return;
            if (_animFrames == null || _animFrames.Length <= 1) return;

            float t = Time.GetTicksMsec() / 1000f;
            int idx = SpriteFrameLoader.FrameIndexAt(t, _animFrames.Length, FrameDurationSec);
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
        var frames = SpriteFrameLoader.LoadFrames(FramePathFormat, fallbackPath: FallbackPath);
        var tex = frames != null && frames.Length > 0 ? frames[0] : null;
        if (tex == null) return null;

        var size = new Vector2(56, 60);

        var root = new MarginContainer
        {
            Name = NodeName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
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
        label.OffsetTop = 42;
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
        flame.AddChild(label);

        var arch = Widgets.CreateArchLabel("WINSTREAK",
            fontId: "fightkid",
            fontSize: 11,
            color: new Color(1f, 0.78f, 0.22f),
            radius: 34f,
            arcDegrees: 135f,
            centerOffsetY: 6f);
        flame.AddChild(arch);
        return root;
    }
}
