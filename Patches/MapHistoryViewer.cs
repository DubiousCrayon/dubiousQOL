using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.addons.mega_text;

namespace dubiousQOL.Patches;

/// <summary>
/// Run-history map viewer that reuses the real NMapScreen scene so the layout,
/// curved dotted paths, and sharpie drawings line up pixel-for-pixel with the
/// original in-run map. The MapHistoryScreenGuard Harmony prefixes keep NMapScreen
/// from wiring into singletons that don't exist outside an active run; we then
/// inject a stub RunState + IPlayerCollection via reflection so SetMap/LoadDrawings
/// operate on our captured data.
/// </summary>
internal static class MapHistoryViewerModal
{
    public static void Open(RunHistory history, Node? runHistoryScreen = null)
    {
        var modal = NModalContainer.Instance;
        if (modal == null) return;
        var sidecar = MapHistoryIO.Read(history.StartTime);
        if (sidecar == null || sidecar.Acts.Count == 0)
        {
            modal.Add(ErrorPanel("No map data available for this run."));
            return;
        }
        try { modal.Add(new MapHistoryViewer(history, sidecar, runHistoryScreen), showBackstop: false); }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"MapHistory open: {e.Message}\n{e.StackTrace}");
            modal.Add(ErrorPanel("Failed to open map viewer (see logs)."));
        }
    }

    private static Control ErrorPanel(string msg)
    {
        var root = new Control { Name = "DubiousMapHistoryError", MouseFilter = Control.MouseFilterEnum.Stop };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var frame = new PanelContainer { Name = "Frame" };
        frame.AnchorLeft = 0.5f; frame.AnchorRight = 0.5f;
        frame.AnchorTop = 0.5f; frame.AnchorBottom = 0.5f;
        frame.OffsetLeft = -220; frame.OffsetRight = 220;
        frame.OffsetTop = -80; frame.OffsetBottom = 80;
        root.AddChild(frame);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 14);
        frame.AddChild(vbox);

        var lbl = new Label { Text = msg, HorizontalAlignment = HorizontalAlignment.Center };
        lbl.AddThemeFontSizeOverride("font_size", 18);
        vbox.AddChild(lbl);

        var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(140, 36) };
        close.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        close.Pressed += () => NModalContainer.Instance?.Clear();
        vbox.AddChild(close);
        return root;
    }
}

internal partial class MapHistoryViewer : Control, IScreenContext
{
    private const string MapScreenScenePath = "res://scenes/screens/map/map_screen.tscn";

    public Control? DefaultFocusedControl => null;

    private readonly RunHistory _history;
    private readonly MapHistorySidecar _sidecar;
    private readonly Node? _runHistoryScreen;
    private int _cursor;

    private MegaLabel _titleLabel = null!;
    private Control? _prevArrow;
    private Control? _nextArrow;
    private Control _stage = null!;
    private NMapScreen? _currentScreen;
    private RunState? _stubRunState;

    public MapHistoryViewer(RunHistory history, MapHistorySidecar sidecar, Node? runHistoryScreen = null)
    {
        _history = history;
        _sidecar = sidecar;
        _runHistoryScreen = runHistoryScreen;
        Name = "DubiousMapHistoryViewer";
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.FullRect);
        Build();
        MapHistoryScreenGuard.Active = true;
        TreeExited += () => MapHistoryScreenGuard.Active = false;
    }

    // Render after the viewer itself is in the scene tree. Rendering from the
    // constructor fails silently because the NMapScreen we instantiate never
    // gets its _Ready called when none of its ancestors are in the tree yet,
    // leaving NNormalMapPoint._iconContainer null and SetAngle throwing NRE.
    public override void _Ready()
    {
        // Back button must be added after the viewer is in the tree so the clone's
        // _Ready fires before we call Enable() — otherwise OnEnable() dereferences
        // null _outline/_buttonImage/_moveTween (silently NREs into the fallback).
        if (!TryBuildBackButton())
        {
            var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(140, 44) };
            close.AnchorLeft = 0f; close.AnchorRight = 0f;
            close.AnchorTop = 1f; close.AnchorBottom = 1f;
            close.OffsetLeft = 24; close.OffsetRight = 164;
            close.OffsetTop = -84; close.OffsetBottom = -40;
            close.Pressed += () => NModalContainer.Instance?.Clear();
            AddChild(close);
        }

        Render();
    }

    private void Build()
    {
        // No backdrop — the viewer's own MouseFilter.Stop already blocks clicks
        // from reaching the run-history screen underneath, so a fullscreen ColorRect
        // would only obscure the parchment background for no functional gain.

        // Header across the top.
        var header = new HBoxContainer { Name = "Header" };
        header.AddThemeConstantOverride("separation", 16);
        header.AnchorLeft = 0f; header.AnchorRight = 1f;
        header.AnchorTop = 0f; header.AnchorBottom = 0f;
        header.OffsetLeft = 32; header.OffsetRight = -32;
        header.OffsetTop = 24; header.OffsetBottom = 84;
        AddChild(header);

        // Styled MegaLabel matching the top-bar ActNameDisplay; per-act font/color
        // is applied in Render() once we know which act we're showing. ExpandFill
        // alone keeps it centered now that header has only this child.
        _titleLabel = ActNameLabel.CreateBlank() ?? throw new InvalidOperationException("Failed to create header label");
        _titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _titleLabel.VerticalAlignment = VerticalAlignment.Center;
        _titleLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _titleLabel.CustomMinimumSize = new Vector2(0, 64);
        header.AddChild(_titleLabel);

        // Stage must fill the full viewport — NMapScreen's internal layout and
        // NMapDrawings' coordinate system are keyed to a 1080-tall container;
        // any vertical inset here scales captured strokes out of alignment with
        // the replayed map. The header overlays on top instead (see below).
        _stage = new Control { Name = "Stage", MouseFilter = MouseFilterEnum.Pass };
        _stage.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_stage);

        // Side-anchored gold arrows duplicated from the underlying NRunHistory
        // screen — same look as the prev/next-run arrows. Added after _stage so
        // they render above the map. Hidden until Render() knows cursor bounds.
        BuildSideArrows();

        // Keep the header above the stage and arrows so its buttons remain clickable.
        // Back button is added later in _Ready (it needs the viewer to be in the tree
        // first; see TryBuildBackButton).
        MoveChild(header, GetChildCount() - 1);
    }

    // Esc-key escape hatch — guarantees the modal can always be dismissed even
    // if the back-button render path fails (e.g. NBackButton clone hidden, hotkey
    // collision swallowed). Without this the player can get trapped.
    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true } k && k.Keycode == Key.Escape)
        {
            NModalContainer.Instance?.Clear();
            GetViewport().SetInputAsHandled();
        }
    }

    private bool TryBuildBackButton()
    {
        try
        {
            // NBackButton lives on a parent screen of NRunHistory. Walk up the
            // run-history screen's ancestors searching subtrees; fall back to a
            // tree-wide search from the viewport root.
            NBackButton? src = null;
            Node? probe = _runHistoryScreen;
            while (probe != null && src == null)
            {
                src = UiHelper.FindFirst<NBackButton>(probe);
                probe = probe.GetParent();
            }
            if (src == null) src = UiHelper.FindFirst<NBackButton>(GetTree().Root);
            if (src == null) { MainFile.Logger.Warn("MapHistory BuildBackButton: no NBackButton in tree"); return false; }

            // Flags=4 → DUPLICATE_SCRIPTS only, skips Connect-style signal bindings
            // so the original screen's back handler isn't carried into our copy.
            // Children (Outline, Image) are duplicated regardless.
            var clone = src.Duplicate(4) as NBackButton;
            if (clone == null) { MainFile.Logger.Warn("MapHistory BuildBackButton: Duplicate cast failed"); return false; }
            clone.Name = "DubiousMapBackButton";
            clone.Released += _ => NModalContainer.Instance?.Clear();
            AddChild(clone); // viewer is in tree by now → clone._Ready fires synchronously
            // _Ready already computed _showPos/_hidePos from the duplicated OffsetLeft/Bottom
            // (which match the in-game back button's anchor since we cloned from it), so
            // just trigger the slide-in.
            clone.Enable();
            return true;
        }
        catch (Exception e) { MainFile.Logger.Warn($"MapHistory BuildBackButton: {e.Message}\n{e.StackTrace}"); }
        return false;
    }

    private void BuildSideArrows()
    {
        if (_runHistoryScreen == null) return;
        try
        {
            var srcLeft = _runHistoryScreen.GetNodeOrNull<Node>("LeftArrow");
            var srcRight = _runHistoryScreen.GetNodeOrNull<Node>("RightArrow");
            if (srcLeft == null || srcRight == null) return;

            // Flags=4 → DUPLICATE_SCRIPTS only. Skipping DUPLICATE_SIGNALS avoids
            // carrying the source NRunHistory.OnLeftButtonButtonReleased Connect
            // binding, which would advance to the prev/next *run* whenever our
            // arrow is clicked. Children are duplicated regardless of flags.
            _prevArrow = srcLeft.Duplicate(4) as Control;
            _nextArrow = srcRight.Duplicate(4) as Control;
            if (_prevArrow == null || _nextArrow == null) return;

            ConfigureSideArrow(_prevArrow, isLeft: true);
            ConfigureSideArrow(_nextArrow, isLeft: false);

            AddChild(_prevArrow);
            AddChild(_nextArrow);

            if (_prevArrow is NClickableControl prevClick)
                prevClick.Released += _ => CycleAct(-1);
            if (_nextArrow is NClickableControl nextClick)
                nextClick.Released += _ => CycleAct(+1);
        }
        catch (Exception e) { MainFile.Logger.Warn($"MapHistory BuildSideArrows: {e.Message}"); }
    }

    private static void ConfigureSideArrow(Control arrow, bool isLeft)
    {
        arrow.Name = isLeft ? "DubiousMapPrevArrow" : "DubiousMapNextArrow";
        arrow.Visible = false;
        // Anchor to the vertical center of the corresponding edge. Width/height
        // come from the duplicated scene's CustomMinimumSize / TextureRect size.
        arrow.AnchorLeft = isLeft ? 0f : 1f;
        arrow.AnchorRight = isLeft ? 0f : 1f;
        arrow.AnchorTop = 0.5f;
        arrow.AnchorBottom = 0.5f;
        const float halfH = 60f;
        const float width = 120f;
        const float edgeInset = 64f;
        arrow.OffsetTop = -halfH;
        arrow.OffsetBottom = halfH;
        if (isLeft) { arrow.OffsetLeft = edgeInset; arrow.OffsetRight = edgeInset + width; }
        else { arrow.OffsetLeft = -(edgeInset + width); arrow.OffsetRight = -edgeInset; }

        if (arrow is NRunHistoryArrowButton arrowBtn) arrowBtn.IsLeft = isLeft;
    }

    private void CycleAct(int delta)
    {
        int next = _cursor + delta;
        if (next < 0 || next >= _sidecar.Acts.Count) return;
        _cursor = next;
        Render();
    }

    private void Render()
    {
        // Clear any prior NMapScreen instance.
        if (_currentScreen != null)
        {
            _currentScreen.QueueFree();
            _currentScreen = null;
        }
        foreach (var child in _stage.GetChildren()) child.QueueFree();

        var act = _sidecar.Acts[_cursor];
        if (_prevArrow != null) _prevArrow.Visible = _cursor > 0;
        if (_nextArrow != null) _nextArrow.Visible = _cursor < _sidecar.Acts.Count - 1;

        var headerActModel = ResolveActModel(act.Index);
        if (headerActModel != null)
            ActNameLabel.ApplyStyle(_titleLabel, headerActModel.Id.Entry, headerActModel.Title.GetFormattedText(), maxFontSizeOverride: 56);

        if (act.Map == null || act.Map.Points.Count == 0)
        {
            var empty = new Label
            {
                Text = "(No map data for this act)",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            empty.SetAnchorsPreset(LayoutPreset.FullRect);
            _stage.AddChild(empty);
            return;
        }

        try { MountMapScreen(act); }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"MapHistory mount: {e.Message}\n{e.StackTrace}");
            var err = new Label { Text = $"Failed to render map for act {act.Index + 1}.", HorizontalAlignment = HorizontalAlignment.Center };
            err.SetAnchorsPreset(LayoutPreset.FullRect);
            _stage.AddChild(err);
        }
    }

    private void MountMapScreen(MapHistorySidecarAct act)
    {
        var actModel = ResolveActModel(act.Index);
        if (actModel == null) throw new InvalidOperationException("Could not resolve ActModel for act index " + act.Index);

        // Build a stub RunState. CreateForTest gives us the ambient plumbing
        // (Rng, Odds, Players, Modifiers) that SetMap pokes at. We use an
        // empty seed — only affects map jitter, which is fine because we're
        // reproducing the saved topology, not re-rolling it.
        var players = new[] { ResolveRunPlayer() };
        // CreateForTest takes canonical ActModel[] and calls ToMutable itself; passing a
        // mutable one trips AssertCanonical.
        var acts = new[] { actModel };
        _stubRunState = RunState.CreateForTest(players: players, acts: acts, ascensionLevel: _history.Ascension);

        // RoomSet.Boss getter throws if _boss is null, which kills NBossMapPoint._Ready
        // silently and leaves the boss node invisible. Replay the captured encounter ID
        // onto the freshly-mutable Act so the boss point can resolve its texture/spine.
        TryApplyBossEncounter(_stubRunState.Act, act.BossEncounterId, act.SecondBossEncounterId);

        // Replay visited coords so SetMap's traveled-path highlighting reproduces
        // what the player actually walked.
        foreach (var coord in act.VisitedMapCoords)
            _stubRunState.AddVisitedMapCoord(coord);

        // Guard must be active before the scene _Ready fires.
        MapHistoryScreenGuard.Active = true;

        var scene = PreloadManager.Cache.GetScene(MapScreenScenePath);
        var screen = scene.Instantiate<NMapScreen>(PackedScene.GenEditState.Disabled);
        screen.Name = "DubiousMapHistoryScreen";
        screen.Visible = true;
        _stage.AddChild(screen); // triggers _Ready (guarded to the minimal version)
        _currentScreen = screen;

        // Re-wire essentials post-AddChild — guard should have done this, but doing
        // it unconditionally makes the path resilient to Harmony ordering quirks.
        MapHistoryScreenGuard.WireEssentialFields(screen);

        // Hide in-run-only UI: the drawing toolbar (draw/eraser/clear buttons don't
        // work in the viewer since there's no net service), the controller hotkey
        // glyph, and the full MapLegend (it's right-anchored and overlaps the next-
        // act arrow).
        HideNode(screen, "%DrawingTools");
        HideNode(screen, "DrawingToolsHotkey");
        HideNode(screen, "MapLegend");

        // State field the rest of NMapScreen relies on.
        SetPrivateField(screen, "_runState", _stubRunState);

        // NMapBg.Initialize stores the RunState so OnVisibilityChanged can read
        // MapTopBg/MapMidBg/MapBotBg. Without it, the bg stays blank. OnVisibilityChanged
        // is the only thing that actually sets the textures, and it only fires when
        // visibility flips — so call it explicitly via reflection after Initialize.
        var bg = GetPrivateField<NMapBg>(screen, "_mapBgContainer");
        if (bg != null)
        {
            bg.Initialize(_stubRunState);
            try
            {
                var m = typeof(NMapBg).GetMethod("OnVisibilityChanged",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                m?.Invoke(bg, null);
            }
            catch (Exception e) { MainFile.Logger.Warn($"MapHistory NMapBg paint: {e.Message}"); }
        }

        // NMapMarker.Initialize needs a Player to pick its texture.
        var marker = GetPrivateField<NMapMarker>(screen, "_marker");
        marker?.Initialize(players[0]);

        // NMapDrawings needs _playerCollection for LoadDrawings color lookup.
        // RunState implements IPlayerCollection directly.
        var drawings = screen.Drawings;
        if (drawings != null)
        {
            SetPrivateField(drawings, "_playerCollection", _stubRunState);
        }

        // Reconstruct the saved ActMap and hand it to the real SetMap — this is
        // what draws the node grid, curved dotted paths, jitter, and visited
        // coloring using the game's own code.
        var savedMap = new SavedActMap(act.Map);
        uint seed = 0;
        try { seed = unchecked((uint)_history.Seed.GetHashCode()); } catch { }

        // NBossMapPoint._Ready reads _runState.Map.SecondBossMapPoint while being added
        // to the tree by SetMap — must be set first or that access NREs and the point
        // skips initializing its placeholder image.
        _stubRunState.Map = savedMap;

        screen.SetMap(savedMap, seed, clearDrawings: false);

        // During a real run, StartOfActAnim lerps _mapContainer.Position from
        // (0, 1800) to (0, -600) — that -600 is the resting position drawings
        // were captured relative to. We bypass that animation, so pin both the
        // current position and the drag target to the in-run resting value,
        // otherwise the map sits ~600px lower than where strokes were drawn.
        var mapContainer = GetPrivateField<Control>(screen, "_mapContainer");
        if (mapContainer != null) mapContainer.Position = new Vector2(0f, -600f);
        SetPrivateField(screen, "_targetDragPos", new Vector2(0f, -600f));
        // TODO: drawings still land a few pixels off from their in-run anchor
        // points on some runs. Deferring OnWindowChange gets most cases aligned
        // but not all — the remaining offset likely comes from a layout/size
        // dependency on NMapDrawings.Size itself (used in FromNetPosition) that
        // settles later than the bg reposition. Revisit with an explicit
        // resize-then-reposition pass, or snapshot the drawings' Size at capture
        // time and apply it here.
        //
        // NMapBg.OnWindowChange positions the bg based on window aspect AND calls
        // Drawings.RepositionBasedOnBackground. It runs during NMapBg._Ready, but
        // at that point the scene's layout pass hasn't finished — Size values for
        // mapBg and Drawings aren't final, so drawings anchor to stale offsets.
        // Defer to the end of the frame so layout has settled, then run it twice
        // across two frames (Godot sometimes needs two passes for VBoxContainer
        // child sizing to stabilize).
        if (bg != null)
        {
            bg.CallDeferred("OnWindowChange");
            GetTree().CreateTimer(0.05).Timeout += () =>
            {
                try { if (IsInstanceValid(bg)) bg.Call("OnWindowChange"); }
                catch (Exception e) { MainFile.Logger.Warn($"MapHistory deferred OnWindowChange: {e.Message}"); }
            };
        }

        // Replay the sharpie drawings on top. Eraser strokes work because we're
        // using the game's own NMapDrawings.LoadDrawings path.
        if (act.Drawings != null)
        {
            try { drawings?.LoadDrawings(act.Drawings); }
            catch (Exception e) { MainFile.Logger.Warn($"MapHistory LoadDrawings: {e.Message}"); }
        }

        // NMapDrawings is a sibling of TheMap/Points and sits above it in the scene
        // tree — its MouseFilter.Stop swallows hover events meant for NMapPoints
        // underneath. In-run this is desired for stroke capture; in the viewer we
        // block drawing input anyway (PatchProcessMouseDrawingEvent), so let mouse
        // events fall through to the points for hover.
        if (drawings != null) drawings.MouseFilter = MouseFilterEnum.Ignore;

        // Wire the same per-node summary tooltip shown on the run history page.
        // RunHistory.MapPointHistory[actIdx][coord.row] is indexed by grid row (one
        // visited node per row). NMapPoint.OnFocus does this in-run but only on
        // controller/keyboard focus — we want mouse hover, so wire it explicitly.
        try { WireHistoryHovers(screen, act); }
        catch (Exception e) { MainFile.Logger.Warn($"MapHistory hover wire: {e.Message}"); }

    }

    private void WireHistoryHovers(NMapScreen screen, MapHistorySidecarAct act)
    {
        if (_history.MapPointHistory == null) return;
        if (act.Index < 0 || act.Index >= _history.MapPointHistory.Count) return;
        var actHistory = _history.MapPointHistory[act.Index];
        if (actHistory == null || actHistory.Count == 0) return;

        // Floor numbering across acts: floor N in the tooltip header is the running
        // count from act 1 row 1, matching the in-run formula in NMapPoint.OnFocus.
        int floorBase = 0;
        for (int i = 0; i < act.Index && i < _history.MapPointHistory.Count; i++)
            floorBase += _history.MapPointHistory[i].Count;

        var visited = new HashSet<MapCoord>(act.VisitedMapCoords);

        var pointsContainer = GetPrivateField<Control>(screen, "_points");
        if (pointsContainer == null) return;

        foreach (var child in pointsContainer.GetChildren())
        {
            if (child is not NMapPoint point) continue;
            var coord = point.Point.coord;
            if (!visited.Contains(coord))
            {
                // Suppress hover state and tooltip for non-visited nodes — the
                // in-run map shows hover on travelable nodes too, but in the
                // viewer there's nothing meaningful to hover on those.
                point.MouseFilter = MouseFilterEnum.Ignore;
                continue;
            }
            int row = coord.row;
            if (row < 0 || row >= actHistory.Count) continue;
            var entry = actHistory[row];
            if (entry == null) continue;
            // Pull playerId from the entry itself rather than RunHistory.Players[0]
            // — NMapPointHistoryHoverTip throws if the id isn't found in
            // entry.PlayerStats, and the entry is the source of truth for who
            // has stats here.
            ulong playerId = entry.PlayerStats?.FirstOrDefault()?.PlayerId ?? 0uL;
            int floorNum = floorBase + row + 1;

            var capturedPoint = point;
            var capturedEntry = entry;
            int capturedFloor = floorNum;
            ulong capturedPlayerId = playerId;
            point.MouseEntered += () =>
            {
                // NButton.OnFocus would normally trigger the hover SFX, but in the
                // viewer the focus system isn't active for map points (no controller
                // focus routing and NMapScreen isn't the ActiveScreen), so fire it
                // manually to match the in-game feel.
                SfxCmd.Play("event:/sfx/ui/clicks/ui_hover");
                ShowHistoryHover(capturedPoint, capturedEntry, capturedPlayerId, capturedFloor);
            };
            point.MouseExited += () => { try { NHoverTipSet.Remove(capturedPoint); } catch { } };
        }
    }

    // Reparent the NHoverTipSet from NGame.HoverTipsContainer into our viewer so it
    // renders above the modal backdrop. The game's stock container sits below
    // NModalContainer in z-order, so otherwise the tip is created correctly but
    // hidden behind our modal panel.
    private void ShowHistoryHover(NMapPoint point, MapPointHistoryEntry entry, ulong playerId, int floorNum)
    {
        try
        {
            var tip = NMapPointHistoryHoverTip.Create(floorNum, playerId, entry);
            var set = NHoverTipSet.CreateAndShowMapPointHistory(point, tip);
            var oldParent = set.GetParent();
            if (oldParent != null && oldParent != this)
            {
                oldParent.RemoveChild(set);
                AddChild(set);
                set.ZIndex = 4096;
            }
            set.SetAlignment(point, HoverTip.GetHoverTipAlignment(point));
        }
        catch (Exception e) { MainFile.Logger.Warn($"MapHistory hover show: {e.Message}\n{e.StackTrace}"); }
    }

    private static void TryApplyBossEncounter(ActModel mutableAct, string? bossId, string? secondBossId)
    {
        try
        {
            if (!string.IsNullOrEmpty(bossId))
            {
                var boss = ModelDb.GetById<EncounterModel>(ModelId.Deserialize(bossId));
                if (boss != null) mutableAct.SetBossEncounter(boss);
            }
            else
            {
                // Sidecar predates encounter capture: pick any boss from the act so the
                // getter doesn't throw. Visual will be wrong but the node renders.
                var fallback = mutableAct.AllBossEncounters.FirstOrDefault();
                if (fallback != null) mutableAct.SetBossEncounter(fallback);
            }
            if (!string.IsNullOrEmpty(secondBossId))
            {
                var sb = ModelDb.GetById<EncounterModel>(ModelId.Deserialize(secondBossId));
                if (sb != null) mutableAct.SetSecondBossEncounter(sb);
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"MapHistory ApplyBossEncounter: {e.Message}"); }
    }

    private ActModel? ResolveActModel(int actIndex)
    {
        try
        {
            if (actIndex < 0 || actIndex >= _history.Acts.Count) return null;
            return ModelDb.GetById<ActModel>(_history.Acts[actIndex]);
        }
        catch { return null; }
    }

// Build a Player matching (as closely as we can) the first player of the run.
    // Map drawings are keyed by player id — if we can echo that id, LoadDrawings
    // won't warn about missing players and the stroke color matches capture.
    private Player ResolveRunPlayer()
    {
        try
        {
            if (_history.Players.Count > 0)
            {
                var rp = _history.Players[0];
                var characterModel = ModelDb.GetById<CharacterModel>(rp.Character);
                if (characterModel != null)
                {
                    // CreateForNewRun<T> requires a generic arg; fall back to reflection via MakeGenericMethod.
                    var player = CreatePlayerForCharacter(characterModel, rp.Id);
                    if (player != null) return player;
                }
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"MapHistory ResolveRunPlayer: {e.Message}"); }
        // Fallback: default Deprived at id 1.
        return Player.CreateForNewRun<Deprived>(UnlockState.all, 1uL);
    }

    private static void HideNode(Node root, string path)
    {
        try
        {
            var n = root.GetNodeOrNull<CanvasItem>(path);
            if (n != null) n.Visible = false;
        }
        catch (Exception e) { MainFile.Logger.Warn($"MapHistory HideNode {path}: {e.Message}"); }
    }

    private static void SetPrivateField(object target, string name, object? value)
    {
        var f = target.GetType().GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        if (f != null) f.SetValue(target, value);
    }

    private static T? GetPrivateField<T>(object target, string name) where T : class
    {
        var f = target.GetType().GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        return f?.GetValue(target) as T;
    }

    private static Player? CreatePlayerForCharacter(CharacterModel model, ulong netId)
    {
        try
        {
            var method = typeof(Player).GetMethod("CreateForNewRun",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method == null) return null;
            var generic = method.MakeGenericMethod(model.GetType());
            return (Player?)generic.Invoke(null, new object[] { UnlockState.all, netId });
        }
        catch { return null; }
    }
}
