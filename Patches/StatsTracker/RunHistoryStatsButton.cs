using System;
using System.IO;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;

namespace dubiousQOL.Patches;

/// <summary>
/// Injects a "View Stats" button on the run history detail screen, to the left
/// of the map button (or share button if map isn't present). Enabled only when
/// a stats sidecar exists for the viewed run. Clicking opens the stats viewer modal.
/// </summary>
public static class PatchRunHistoryStatsButton
{
    private const string ButtonName = "DubiousViewStatsButton";

    internal static long CurrentStartTime;
    private static Texture2D? _cachedIcon;

    [HarmonyPatch(typeof(NRunHistory), "_Ready")]
    public static class PatchReady
    {
        [HarmonyPostfix]
        public static void Postfix(NRunHistory __instance)
        {
            if (!StatsTrackerConfig.Instance.Enabled) return;
            try { InjectButton(__instance); }
            catch (Exception e) { MainFile.Logger.Warn($"RunHistoryStatsButton inject: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(NRunHistory), "DisplayRun")]
    public static class PatchDisplayRun
    {
        [HarmonyPostfix]
        public static void Postfix(NRunHistory __instance, RunHistory history)
        {
            if (!StatsTrackerConfig.Instance.Enabled) return;
            try
            {
                CurrentStartTime = history?.StartTime ?? 0;
                var btn = __instance.FindChild(ButtonName, recursive: true, owned: false) as BaseButton;
                if (btn == null || history == null) return;
                bool hasStats = HasSidecar(history.StartTime);
                btn.Disabled = !hasStats;
                btn.MouseFilter = hasStats ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
                if (!hasStats) { btn.Modulate = Colors.White; btn.Scale = Vector2.One; }
                btn.TooltipText = hasStats
                    ? "View run stats breakdown"
                    : "No stats data for this run";
            }
            catch (Exception e) { MainFile.Logger.Warn($"RunHistoryStatsButton toggle: {e.Message}"); }
        }
    }

    private static bool HasSidecar(long startTime)
    {
        var path = StatsTrackerIO.SidecarPath(startTime);
        return path != null && File.Exists(path);
    }

    private static void InjectButton(NRunHistory screen)
    {
        if (screen.FindChild(ButtonName, recursive: true, owned: false) != null) return;

        const float btnSize = 80f;
        const float gap = 28f;

        var iconTex = TryLoadStatsIcon();
        BaseButton btn;
        if (iconTex != null)
        {
            var wrapper = new TextureButton
            {
                Name = ButtonName,
                Disabled = true,
                CustomMinimumSize = new Vector2(btnSize, btnSize),
                MouseFilter = Control.MouseFilterEnum.Stop,
                TooltipText = "View run stats breakdown",
            };
            const float iconScale = 1.5f;
            float scaledSize = btnSize * iconScale;
            float offset = (btnSize - scaledSize) / 2f;
            var texRect = new TextureRect
            {
                Texture = iconTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Position = new Vector2(offset, offset),
                Size = new Vector2(scaledSize, scaledSize),
            };
            wrapper.AddChild(texRect);
            btn = wrapper;
        }
        else
        {
            btn = new Button
            {
                Name = ButtonName,
                Text = "Stats",
                Disabled = true,
                CustomMinimumSize = new Vector2(120, 44),
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            btn.AddThemeFontSizeOverride("font_size", 20);
        }

        // Position relative to the map button (if present) or share button.
        var anchor = screen.FindChild("DubiousViewMapButton", recursive: true, owned: false) as Control
                  ?? UiHelper.FindFirst<NShareButton>(screen) as Control;
        if (anchor != null)
        {
            var anchorParent = anchor.GetParent() as Control ?? screen;
            anchorParent.AddChild(btn);
            btn.AnchorLeft = anchor.AnchorLeft;
            btn.AnchorRight = anchor.AnchorRight;
            btn.AnchorTop = anchor.AnchorTop;
            btn.AnchorBottom = anchor.AnchorBottom;
            btn.OffsetRight = anchor.OffsetLeft - gap;
            btn.OffsetLeft = btn.OffsetRight - btnSize;
            float anchorCenter = (anchor.OffsetTop + anchor.OffsetBottom) * 0.5f;
            btn.OffsetTop = anchorCenter - btnSize * 0.5f;
            btn.OffsetBottom = anchorCenter + btnSize * 0.5f;
        }
        else
        {
            screen.AddChild(btn);
            btn.AnchorLeft = 0f; btn.AnchorRight = 0f;
            btn.AnchorTop = 1f; btn.AnchorBottom = 1f;
            btn.OffsetLeft = 110;
            btn.OffsetRight = 110 + btnSize;
            btn.OffsetTop = -(80 + btnSize);
            btn.OffsetBottom = -80;
        }

        btn.PivotOffset = new Vector2(btnSize * 0.5f, btnSize * 0.5f);
        btn.MouseEntered += () => { if (!btn.Disabled) { btn.Modulate = new Color(1.2f, 1.2f, 1.2f); btn.Scale = new Vector2(1.1f, 1.1f); SfxCmd.Play("event:/sfx/ui/clicks/ui_hover"); } };
        btn.MouseExited += () => { btn.Modulate = Colors.White; btn.Scale = Vector2.One; };
        btn.ButtonDown += () => { if (!btn.Disabled) SfxCmd.Play("event:/sfx/ui/clicks/ui_click"); };

        btn.Pressed += () =>
        {
            try
            {
                if (CurrentStartTime == 0) return;
                var sidecar = StatsTrackerIO.Read(CurrentStartTime);
                if (sidecar == null) return;
                RunHistoryStatsViewerModal.Open(sidecar);
            }
            catch (Exception e) { MainFile.Logger.Warn($"RunHistoryStatsButton open: {e.Message}\n{e.StackTrace}"); }
        };
    }

    private static Texture2D? TryLoadStatsIcon()
    {
        if (_cachedIcon != null) return _cachedIcon;

        // Pluck the chart/graph icon from the compendium's Statistics button.
        // The button scene has a TextureRect child "Icon" with the texture
        // baked into the .tscn. Instantiate once, grab, free, cache.
        try
        {
            var scenePath = SceneHelper.GetScenePath("screens/compendium_submenu");
            if (ResourceLoader.Exists(scenePath))
            {
                var scene = PreloadManager.Cache.GetScene(scenePath);
                var tmp = scene.Instantiate<Node>(PackedScene.GenEditState.Disabled);
                try
                {
                    // StatisticsButton has an "Icon" TextureRect child.
                    // Use FindChild — unique name (%) lookup requires the node to be in the tree.
                    var statsBtn = tmp.FindChild("StatisticsButton", recursive: true, owned: false);
                    var iconRect = statsBtn?.GetNodeOrNull<TextureRect>("Icon");
                    if (iconRect?.Texture is Texture2D tex) { _cachedIcon = tex; return tex; }
                }
                finally { tmp.QueueFree(); }
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"RunHistoryStatsButton icon from compendium: {e.Message}"); }

        // Fallback: stats atlas icon.
        try
        {
            var tex = ResourceLoader.Load<Texture2D>(
                "res://images/atlases/stats_screen_atlas.sprites/stats_swords.tres",
                null, ResourceLoader.CacheMode.Reuse);
            if (tex != null) { _cachedIcon = tex; return tex; }
        }
        catch { }
        return null;
    }
}
