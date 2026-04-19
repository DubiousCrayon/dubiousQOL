using System;
using System.IO;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;

using dubiousQOL.UI;
using dubiousQOL.Utilities;

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
                ButtonHelper.SetToggleState(btn, hasStats,
                    "View run stats breakdown",
                    "No stats data for this run");
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
        var anchorParent = anchor != null ? (anchor.GetParent() as Control ?? screen) : screen;
        ButtonHelper.PositionLeftOf(btn, anchor, anchorParent, btnSize, gap);
        ButtonHelper.WireHoverAndClickSfx(btn, btnSize);

        btn.Pressed += () =>
        {
            try
            {
                if (CurrentStartTime == 0)
                {
                    MainFile.Logger.Warn("RunHistoryStatsButton: CurrentStartTime is 0");
                    return;
                }
                var sidecar = StatsTrackerIO.Read(CurrentStartTime);
                if (sidecar == null)
                {
                    MainFile.Logger.Warn($"RunHistoryStatsButton: sidecar null for {CurrentStartTime}");
                    return;
                }
                RunHistoryStatsViewerModal.Open(sidecar);
            }
            catch (Exception e) { MainFile.Logger.Warn($"RunHistoryStatsButton open: {e.Message}\n{e.StackTrace}"); }
        };
    }

    private static Texture2D? TryLoadStatsIcon()
    {
        if (_cachedIcon != null) return _cachedIcon;

        // Pluck the chart/graph icon from the compendium's Statistics button.
        var scenePath = SceneHelper.GetScenePath("screens/compendium_submenu");
        var tex = NodeHelper.ExtractTextureFromScene(scenePath, "StatisticsButton");
        if (tex != null) { _cachedIcon = tex; return tex; }

        // Fallback: stats atlas icon.
        try
        {
            tex = ResourceLoader.Load<Texture2D>(
                "res://images/atlases/stats_screen_atlas.sprites/stats_swords.tres",
                null, ResourceLoader.CacheMode.Reuse);
            if (tex != null) { _cachedIcon = tex; return tex; }
        }
        catch { }
        return null;
    }
}
