using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;

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
            if (!DubiousConfig.MapHistory) return;
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
            if (!DubiousConfig.MapHistory) return;
            try
            {
                CurrentHistory = history;
                // FindChild recursive=true since the button now lives under the
                // share button's parent, not directly under NRunHistory.
                var btn = __instance.FindChild(ButtonName, recursive: true, owned: false) as BaseButton;
                if (btn == null || history == null) return;
                bool hasMaps = MapHistoryIO.Exists(history.StartTime);
                btn.Disabled = !hasMaps;
                btn.TooltipText = hasMaps
                    ? "View saved per-act maps"
                    : "No map saved for this run (pre-feature run history)";
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

        // Position relative to the in-scene Share button (bottom-right). Reading
        // share's resolved offsets keeps us layout-correct without hardcoding any
        // pixel positions from the .tscn. Falls back to bottom-left if share isn't
        // found (shouldn't happen on the run history screen).
        const float gap = 24f;
        var share = UiHelper.FindFirst<NShareButton>(screen);
        if (share != null)
        {
            var shareParent = share.GetParent() as Control ?? screen;
            shareParent.AddChild(btn);
            btn.AnchorLeft = share.AnchorLeft;
            btn.AnchorRight = share.AnchorRight;
            btn.AnchorTop = share.AnchorTop;
            btn.AnchorBottom = share.AnchorBottom;
            btn.OffsetRight = share.OffsetLeft - gap;
            btn.OffsetLeft = btn.OffsetRight - btnSize;
            // Vertically center to share's box.
            float shareCenter = (share.OffsetTop + share.OffsetBottom) * 0.5f;
            btn.OffsetTop = shareCenter - btnSize * 0.5f;
            btn.OffsetBottom = shareCenter + btnSize * 0.5f;
        }
        else
        {
            screen.AddChild(btn);
            btn.AnchorLeft = 0f; btn.AnchorRight = 0f;
            btn.AnchorTop = 1f; btn.AnchorBottom = 1f;
            if (iconTex != null)
            {
                btn.OffsetLeft = leftInset;
                btn.OffsetRight = leftInset + btnSize;
                btn.OffsetTop = -(bottomInset + btnSize);
                btn.OffsetBottom = -bottomInset;
            }
            else
            {
                btn.OffsetLeft = 96;
                btn.OffsetRight = 276;
                btn.OffsetTop = -96;
                btn.OffsetBottom = -52;
            }
        }

        // Hover state: brighten + scale up slightly. PivotOffset center keeps the
        // scale anchored to the button center instead of top-left.
        btn.PivotOffset = new Vector2(btnSize * 0.5f, btnSize * 0.5f);
        // Raw TextureButton/Button doesn't route through NButton.OnFocus, so the
        // UI hover SFX that NBackButton etc. get for free has to be triggered by hand.
        btn.MouseEntered += () => { if (!btn.Disabled) { btn.Modulate = new Color(1.2f, 1.2f, 1.2f); btn.Scale = new Vector2(1.1f, 1.1f); SfxCmd.Play("event:/sfx/ui/clicks/ui_hover"); } };
        btn.MouseExited += () => { btn.Modulate = Colors.White; btn.Scale = Vector2.One; };

        // Fire the click SFX on mouse-down (matching NButton.OnPress via HandleMousePress),
        // not on Pressed/release. Godot's BaseButton.Pressed defaults to release-mode, so
        // wiring the sound there made it land at the end of the click instead of at the
        // start like every other in-game button.
        btn.ButtonDown += () => { if (!btn.Disabled) SfxCmd.Play("event:/sfx/ui/clicks/ui_click"); };

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
        string[] candidates =
        {
            "res://scenes/ui/top_bar/map_button.tscn",
            "res://scenes/ui/top_bar/top_bar_map_button.tscn",
            "res://scenes/ui/top_bar/map.tscn",
        };
        foreach (var path in candidates)
        {
            try
            {
                if (!ResourceLoader.Exists(path)) continue;
                var packed = ResourceLoader.Load<PackedScene>(path, null, ResourceLoader.CacheMode.Reuse);
                if (packed == null) continue;
                var inst = packed.Instantiate<Node>(PackedScene.GenEditState.Disabled);
                try
                {
                    var iconNode = inst.GetNodeOrNull<TextureRect>("Control/Icon")
                                   ?? inst.GetNodeOrNull<TextureRect>("Icon");
                    if (iconNode?.Texture is Texture2D t) return t;
                }
                finally { inst.QueueFree(); }
            }
            catch (Exception e) { MainFile.Logger.Warn($"MapHistoryButton icon load {path}: {e.Message}"); }
        }
        return null;
    }
}
