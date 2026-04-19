using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;

using dubiousQOL.UI;
using dubiousQOL.Utilities;

namespace dubiousQOL.Patches;

/// <summary>
/// Injects a "View Map" button at bottom-left of the run history screen. The button
/// is enabled only when a {StartTime}.maps.json sidecar exists alongside the run —
/// i.e. only for runs that were completed after Map History shipped. Clicking opens
/// MapHistoryViewerModal with the sidecar data.
/// </summary>
public static class PatchMapHistoryButton
{
    private const string ButtonName = "DubiousViewMapButton";

    // Set by DisplayRun postfix so the button's Pressed closure can see the
    // currently-selected run. Godot Meta can't hold non-Variant C# objects, so
    // a static handoff is the cleanest option.
    internal static RunHistory? CurrentHistory;

    [HarmonyPatch(typeof(NRunHistory), "_Ready")]
    public static class PatchReady
    {
        [HarmonyPostfix]
        public static void Postfix(NRunHistory __instance)
        {
            if (!MapHistoryConfig.Instance.Enabled) return;
            try { InjectButton(__instance); }
            catch (Exception e) { MainFile.Logger.Warn($"MapHistoryButton inject: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(NRunHistory), "DisplayRun")]
    public static class PatchDisplayRun
    {
        [HarmonyPostfix]
        public static void Postfix(NRunHistory __instance, RunHistory history)
        {
            if (!MapHistoryConfig.Instance.Enabled) return;
            try
            {
                CurrentHistory = history;
                var btn = __instance.FindChild(ButtonName, recursive: true, owned: false) as BaseButton;
                if (btn == null || history == null) return;
                bool hasMaps = MapHistoryIO.Exists(history.StartTime);
                ButtonHelper.SetToggleState(btn, hasMaps,
                    "View saved per-act maps",
                    "No map saved for this run (pre-feature run history)");
            }
            catch (Exception e) { MainFile.Logger.Warn($"MapHistoryButton toggle: {e.Message}"); }
        }
    }

    private static void InjectButton(NRunHistory screen)
    {
        if (screen.FindChild(ButtonName, recursive: true, owned: false) != null) return;

        const float btnSize = 80f;
        const float leftInset = 110f;
        const float bottomInset = 80f;

        BaseButton btn;
        var iconTex = TryLoadTopBarMapIcon();
        if (iconTex != null)
        {
            // TextureButton mirrors the in-game top-bar map icon (parchment scroll).
            // Stretched=KeepAspectCentered + IgnoreTextureSize lets us pin the click
            // target size while the texture fits inside it.
            var tb = new TextureButton
            {
                Name = ButtonName,
                Disabled = true,
                CustomMinimumSize = new Vector2(btnSize, btnSize),
                MouseFilter = Control.MouseFilterEnum.Stop,
                TextureNormal = iconTex,
                IgnoreTextureSize = true,
                StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
                TooltipText = "View saved per-act maps",
            };
            btn = tb;
        }
        else
        {
            btn = new Button
            {
                Name = ButtonName,
                Text = "View Map",
                Disabled = true,
                CustomMinimumSize = new Vector2(180, 44),
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            btn.AddThemeFontSizeOverride("font_size", 20);
        }

        // Position relative to the in-scene Share button (bottom-right).
        const float gap = 24f;
        var share = UiHelper.FindFirst<NShareButton>(screen);
        var shareParent = share != null ? (share.GetParent() as Control ?? screen) : screen;
        ButtonHelper.PositionLeftOf(btn, share, shareParent, btnSize, gap, leftInset, bottomInset);
        ButtonHelper.WireHoverAndClickSfx(btn, btnSize);

        btn.Pressed += () =>
        {
            try
            {
                var h = CurrentHistory;
                if (h == null) return;
                MapHistoryViewerModal.Open(h, screen);
            }
            catch (Exception e) { MainFile.Logger.Warn($"MapHistoryButton open: {e.Message}\n{e.StackTrace}"); }
        };
    }

    // Pluck the parchment-scroll icon texture out of the top-bar map button scene,
    // free the rest. The scene path is sibling to the confirmed
    // res://scenes/ui/top_bar/second_boss_icon.tscn (NTopBarBossIcon hardcodes that
    // one), so map_button.tscn at the same dir is the natural guess; tolerate other
    // names without crashing.
    private static Texture2D? TryLoadTopBarMapIcon()
    {
        string[] sceneCandidates =
        {
            "res://scenes/ui/top_bar/map_button.tscn",
            "res://scenes/ui/top_bar/top_bar_map_button.tscn",
            "res://scenes/ui/top_bar/map.tscn",
        };
        foreach (var path in sceneCandidates)
        {
            var tex = NodeHelper.ExtractTextureFromScene(path, "Control/Icon", "Icon");
            if (tex != null) return tex;
        }
        return null;
    }
}
