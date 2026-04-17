using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace dubiousQOL.Patches;

/// <summary>
/// While Active == true, the Harmony prefixes in this file bypass the NMapScreen
/// lifecycle paths that depend on live-run singletons (RunManager, NCapstoneContainer,
/// NControllerManager, NInputManager, MapSelectionSynchronizer). This lets us
/// reuse the real NMapScreen scene for the run-history viewer without touching
/// its behavior during actual gameplay — the flag is only true while a viewer
/// modal is on screen.
///
/// Approach: replace the full _Ready with a minimal version that only wires the
/// child nodes we need (map container, points, paths, marker, drawings), skipping
/// all the event-handler registration. _ExitTree, InitMapVotes, and
/// RefreshAllMapPointVotes are no-ops under the flag.
/// </summary>
internal static class MapHistoryScreenGuard
{
    public static bool Active;

    [HarmonyPatch(typeof(NMapScreen), "_Ready")]
    public static class PatchReady
    {
        [HarmonyPrefix]
        public static bool Prefix(NMapScreen __instance)
        {
            if (!Active) return true;
            try { MinimalReady(__instance); }
            catch (Exception e) { MainFile.Logger.Warn($"MapHistory minimal _Ready: {e.Message}\n{e.StackTrace}"); }
            return false;
        }
    }

    [HarmonyPatch(typeof(NMapScreen), "_ExitTree")]
    public static class PatchExitTree
    {
        [HarmonyPrefix]
        public static bool Prefix() => !Active;
    }

    [HarmonyPatch(typeof(NMapScreen), "InitMapVotes")]
    public static class PatchInitMapVotes
    {
        [HarmonyPrefix]
        public static bool Prefix() => !Active;
    }

    [HarmonyPatch(typeof(NMapScreen), "RefreshAllMapPointVotes")]
    public static class PatchRefreshAllMapPointVotes
    {
        [HarmonyPrefix]
        public static bool Prefix() => !Active;
    }

    [HarmonyPatch(typeof(NMapScreen), "UpdateHotkeyDisplay")]
    public static class PatchUpdateHotkeyDisplay
    {
        [HarmonyPrefix]
        public static bool Prefix() => !Active;
    }

    // TravelToMapCoord dives into NCapstoneContainer/NGame/RunManager singletons — none
    // of which are safe to touch outside a live run. Under Active, short-circuit with
    // a completed Task so a stray node click in the viewer is a no-op rather than NRE.
    [HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.TravelToMapCoord))]
    public static class PatchTravelToMapCoord
    {
        [HarmonyPrefix]
        public static bool Prefix(ref System.Threading.Tasks.Task __result)
        {
            if (!Active) return true;
            __result = System.Threading.Tasks.Task.CompletedTask;
            return false;
        }
    }

    // ProcessMouseDrawingEvent creates an NMapDrawingInput that calls into _netService
    // (null in our stub screen) → NRE. Block it under the flag; ProcessMouseEvent itself
    // is safe to run because IsLocalDrawing is forced false and drag-scroll doesn't
    // touch any null plumbing — letting it run is what makes the viewer scrollable.
    [HarmonyPatch(typeof(NMapScreen), "ProcessMouseDrawingEvent")]
    public static class PatchProcessMouseDrawingEvent
    {
        [HarmonyPrefix]
        public static bool Prefix() => !Active;
    }

    // _Process calls NGame.Instance.RemoteCursorContainer.ForceUpdateAllCursors() —
    // fine during a run, but NGame.Instance can be null-ish in our context. Let the
    // process body run normally for the scroll lerp, but swallow that specific call.
    // Simplest: guard the whole _Process with a try/catch fallback that still runs
    // UpdateScrollPosition for drag behavior. Done via transpiler-free prefix that
    // re-implements the scroll lerp only (position update).

    [HarmonyPatch(typeof(NMapDrawings), "IsLocalDrawing")]
    public static class PatchIsLocalDrawing
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (!Active) return true;
            __result = false;
            return false;
        }
    }

    // IsInputAllowed -> GetLocalDrawingMode hits NMapDrawings._netService (null in
    // our stub screen). Return None so NMapPoint.OnFocus treats input as allowed-no-op.
    [HarmonyPatch(typeof(NMapDrawings), nameof(NMapDrawings.GetLocalDrawingMode))]
    public static class PatchGetLocalDrawingMode
    {
        [HarmonyPrefix]
        public static bool Prefix(ref MegaCrit.Sts2.Core.Nodes.Screens.Map.DrawingMode __result)
        {
            if (!Active) return true;
            __result = MegaCrit.Sts2.Core.Nodes.Screens.Map.DrawingMode.None;
            return false;
        }
    }

    // Defensive: if _placeholderImage wasn't assigned (e.g. _Ready threw), skip the
    // color refresh entirely rather than NREing inside SetMap → RefreshAllPointVisuals.
    [HarmonyPatch(typeof(NBossMapPoint), "RefreshColorInstantly")]
    public static class PatchBossPointRefreshColor
    {
        [HarmonyPrefix]
        public static bool Prefix(NBossMapPoint __instance)
        {
            if (!Active) return true;
            // If either _placeholderImage (fallback art) or _material (spine art) is
            // null, the boss point never finished initializing — no-op this call.
            var img = GetField(__instance, "_placeholderImage");
            var mat = GetField(__instance, "_material");
            var uses = GetField(__instance, "_usesSpine") as bool?;
            bool ready = (uses == true && mat != null) || (uses == false && img != null);
            return ready;
        }
    }

    // NMapPoint.ConnectSignals calls VoteContainer.Initialize(_, _runState.Players)
    // which can throw in our stub context, aborting _Ready before _iconContainer
    // gets wired. We replace the chain with a hand-rolled equivalent that does
    // everything NClickableControl.ConnectSignals + NButton.ConnectSignals + the
    // safe parts of NMapPoint.ConnectSignals do — but skips Initialize.
    //
    // Critical: replicate NClickableControl's signal Connect() calls. Skipping them
    // means MouseEntered/FocusEntered Godot signals never reach OnHoverHandler →
    // RefreshFocus → OnFocus, so the native hover state and tooltip stay dead.
    // Private handlers are reachable via name-based Callable dispatch because
    // InvokeGodotClassMethod is generated for every method.
    [HarmonyPatch(typeof(NMapPoint), "ConnectSignals")]
    public static class PatchMapPointConnectSignals
    {
        [HarmonyPrefix]
        public static bool Prefix(NMapPoint __instance)
        {
            if (!Active) return true;
            try
            {
                // NClickableControl.ConnectSignals equivalent.
                __instance.Connect(Control.SignalName.FocusEntered,
                    new Callable(__instance, NClickableControl.MethodName.OnFocusHandler));
                __instance.Connect(Control.SignalName.FocusExited,
                    new Callable(__instance, NClickableControl.MethodName.OnUnFocusHandler));
                __instance.Connect(Control.SignalName.MouseEntered,
                    new Callable(__instance, NClickableControl.MethodName.OnHoverHandler));
                __instance.Connect(Control.SignalName.MouseExited,
                    new Callable(__instance, NClickableControl.MethodName.OnUnhoverHandler));
                __instance.Connect(NClickableControl.SignalName.MousePressed,
                    new Callable(__instance, NClickableControl.MethodName.HandleMousePress));
                __instance.Connect(NClickableControl.SignalName.MouseReleased,
                    new Callable(__instance, NClickableControl.MethodName.HandleMouseRelease));
                __instance.Connect(CanvasItem.SignalName.VisibilityChanged,
                    new Callable(__instance, NClickableControl.MethodName.OnVisibilityChanged));
                SetField(__instance, "_isControllerNavigable",
                    __instance.FocusMode == Control.FocusModeEnum.All);

                // NButton.ConnectSignals equivalent (sans RegisterHotkeys — NMapPoint
                // doesn't override Hotkeys so it's empty and that branch is a no-op
                // in the original). UpdateControllerButton tolerates a null icon.
                var hotkeyIcon = __instance.GetNodeOrNull<TextureRect>("%ControllerIcon");
                SetField(__instance, "_controllerHotkeyIcon", hotkeyIcon);

                // NMapPoint.ConnectSignals safe parts. Skip VoteContainer.Initialize
                // — it's the call that NREs in stub context.
                var reticle = __instance.GetNodeOrNull<NSelectionReticle>("%SelectionReticle");
                SetField(__instance, "_controllerSelectionReticle", reticle);
                var vote = __instance.GetNodeOrNull<NMultiplayerVoteContainer>("%MapPointVoteContainer");
                SetProperty(__instance, "VoteContainer", vote);
            }
            catch (Exception e) { MainFile.Logger.Warn($"MapHistory NMapPoint ConnectSignals: {e.Message}\n{e.StackTrace}"); }
            return false;
        }
    }

    // Wire only the field set SetMap + draw needs. Uses direct reflection to bypass
    // Traverse.FieldExists quirks on private base-class visibility.
    internal static void WireEssentialFields(NMapScreen s)
    {
        SetField(s, "_mapContainer", s.GetNodeOrNull<Control>("TheMap"));
        SetField(s, "_mapBgContainer", s.GetNodeOrNull<NMapBg>("%MapBg"));
        SetField(s, "_pathsContainer", s.GetNodeOrNull<Control>("TheMap/Paths"));
        SetField(s, "_points", s.GetNodeOrNull<Control>("TheMap/Points"));
        SetField(s, "_marker", s.GetNodeOrNull<NMapMarker>("TheMap/MapMarker"));
        var drawings = s.GetNodeOrNull<NMapDrawings>("TheMap/Drawings");
        SetProperty(s, "Drawings", drawings);
        // Must stay false — NMapScreen.CanScroll() returns !_isInputDisabled,
        // and we need drag-scroll to work in the viewer. Click-to-travel is
        // neutralized by the TravelToMapCoord prefix below instead.
        SetField(s, "_isInputDisabled", false);
        MainFile.Logger.Info($"MapHistory WireEssential: mapContainer={GetField(s, "_mapContainer") != null} points={GetField(s, "_points") != null} paths={GetField(s, "_pathsContainer") != null} marker={GetField(s, "_marker") != null} bg={GetField(s, "_mapBgContainer") != null} drawings={s.Drawings != null}");
    }

    private static void MinimalReady(NMapScreen s)
    {
        WireEssentialFields(s);
    }

    private static void SetField(object target, string name, object? value)
    {
        var f = target.GetType().GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        if (f != null) f.SetValue(target, value);
        else MainFile.Logger.Warn($"MapHistory SetField missing: {name}");
    }

    private static object? GetField(object target, string name)
    {
        var f = target.GetType().GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        return f?.GetValue(target);
    }

    private static void SetProperty(object target, string name, object? value)
    {
        var p = target.GetType().GetProperty(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        if (p != null && p.CanWrite) p.SetValue(target, value);
        else MainFile.Logger.Warn($"MapHistory SetProperty missing: {name}");
    }
}
