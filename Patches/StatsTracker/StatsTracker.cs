using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace dubiousQOL.Patches;

internal enum DmMetric { DamageDealt, BlockGained, HpLost }
internal enum DmScope { Combat, Act, Run }

[HarmonyPatch]
public static class PatchStatsTracker
{
    internal static StatsTrackerOverlay? Overlay { get; private set; }

    [HarmonyPatch(typeof(NRun), "_Ready")]
    [HarmonyPostfix]
    public static void AfterRunReady(NRun __instance)
    {
        if (!StatsTrackerConfig.Instance.Enabled) return;
        try
        {
            Dispose();
            var globalUi = __instance.GlobalUi;
            if (globalUi == null) return;
            Overlay = new StatsTrackerOverlay
            {
                // Stay responsive (F8, close button) even when the capstone
                // pauses combat — parent inherits the paused state otherwise.
                ProcessMode = Node.ProcessModeEnum.Always,
            };
            // Mount inside GlobalUi (default canvas) so tooltips — which live
            // on NGame.HoverTipsContainer, a later sibling of GlobalUi's chain
            // in NGame's children — naturally draw above us via tree order.
            // Previously used a CanvasLayer (Layer>=0 always drew above the
            // default canvas, so tooltips got covered). Deferred so NRun's own
            // _Ready finishes first. Last-child position keeps us over TopBar
            // and the rest of the in-run HUD like the old CanvasLayer did.
            globalUi.CallDeferred(Node.MethodName.AddChild, Overlay);
        }
        catch (Exception e) { MainFile.Logger.Warn($"StatsTracker mount: {e.Message}"); }
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    [HarmonyPostfix]
    public static void AfterCleanUp()
    {
        try { Dispose(); }
        catch (Exception e) { MainFile.Logger.Warn($"StatsTracker unmount: {e.Message}"); }
    }

    private static void Dispose()
    {
        if (Overlay != null && GodotObject.IsInstanceValid(Overlay))
            Overlay.QueueFree();
        Overlay = null;
    }
}

internal sealed partial class StatsTrackerOverlay : Control
{
    private const float DefaultRightMargin = 2f;
    private const float MinW = 250f;

    // Fixed chrome (title+tabs+metric+agg+sep + vbox separations + bottom pad).
    // Measured from BuildUi; bump if you add another row to the header block.
    private const float HeaderHeightExpanded = 145f;
    private const float HeaderHeightCollapsed = 60f;
    private const float RowStrideH = 24f; // RowH (22) + rowBox separation (2)
    private const float TitleH = 22f;
    private const float RowH = 22f;
    private const float Pad = 1f;
    private const float PadLarge = 3f;

    private static readonly string[] MetricNames = { "Damage Dealt", "Block Gained", "HP Lost" };
    private static readonly string[] ScopeNames = { "Combat", "Act", "Run" };

    private static readonly Color BgColor = new(0.06f, 0.06f, 0.10f, 0.90f);
    private static readonly Color TitleColor = new(0.10f, 0.10f, 0.16f, 0.95f);
    private static readonly Color BorderColor = new(0.3f, 0.3f, 0.4f, 0.5f);
    private static readonly Color AccentColor = new(0.9f, 0.85f, 0.6f);
    // Indexed by DmMetric: DamageDealt, BlockGained, HpLost.
    private static readonly Color[] MetricAccentColors =
    {
        new(0.82f, 0.30f, 0.32f), // dark red — damage dealt
        new(0.55f, 0.80f, 0.95f), // ice blue — block
        new(0.80f, 0.60f, 0.75f), // dusty mauve — hp lost
    };
    private static readonly Color AggAccentColor = new(0.95f, 0.65f, 0.35f);
    private static readonly Color SeparatorColor = new(0.85f, 0.85f, 0.9f);
    private static readonly Color TabActiveColor = new(0.25f, 0.25f, 0.35f, 0.9f);
    private static readonly Color TabInactiveColor = new(0.12f, 0.12f, 0.18f, 0.7f);
    private static readonly Color BarBgColor = new(0.12f, 0.12f, 0.18f, 0.6f);

    private DmMetric _metric = DmMetric.DamageDealt;
    private DmScope _scope = DmScope.Combat;
    private bool _perTurn = false;
    private bool _filtersCollapsed = true;

    // Split "is it Visible?" into two sources: _userVisible reflects F8/close-button
    // intent; _menuHiding reflects capstone (pause/compendium/settings) being open.
    // The overlay shows iff the user hasn't dismissed it AND no capstone is up.
    private bool _userVisible = true;
    private bool _menuHiding;

    private float CurrentHeaderHeight => _filtersCollapsed ? HeaderHeightCollapsed : HeaderHeightExpanded;

    private enum ResizeEdge { BottomRight, BottomLeft, Bottom }

    private bool _dragging;
    private Vector2 _dragOffset;
    private bool _resizing;
    private ResizeEdge _resizeEdge;
    private Vector2 _resizeStart;
    private Vector2 _sizeStart;
    private Vector2 _positionStart;
    private bool _userResized;
    private float _contentHeight;

    private Label _metricLabel = null!;
    private VBoxContainer _rowBox = null!;
    private readonly Button[] _scopeBtns = new Button[3];
    private Button _perTurnBtn = null!;
    private Button _cumulativeBtn = null!;
    private Control _aggRow = null!;
    private Control _summaryRow = null!;
    private Control _scopeTabs = null!;
    private Control _metricSelector = null!;
    private RichTextLabel _summaryLabel = null!;
    private Label _summaryChevron = null!;
    private Control? _breakdownPopup;

    public StatsTrackerOverlay()
    {
        Name = "DubiousStatsTracker";
        Size = new Vector2(StatsTrackerConfig.Instance.DefaultWidth, CurrentHeaderHeight + RowStrideH);
        MouseFilter = MouseFilterEnum.Ignore;
        BuildUi();
        Refresh();
    }

    public override void _Ready()
    {
        base._Ready();
        // Anchor default position to the top-right of the viewport, below the
        // banner. Uses viewport size at mount time; the user can then drag
        // freely and resize from the bottom-right handle.
        var vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
        Position = new Vector2(vp.X - Size.X - DefaultRightMargin, StatsTrackerConfig.Instance.DefaultTopMargin);
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        StatsTrackerData.Updated += OnTrackerUpdated;
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        StatsTrackerData.Updated -= OnTrackerUpdated;
    }

    private void OnTrackerUpdated()
    {
        if (!IsInstanceValid(this)) return;
        // Defer: Changed fires from inside game logic; rebuilding UI mid-tick can
        // disturb parent layout or recurse if any child input triggers another event.
        CallDeferred(MethodName.Refresh);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Node-level _UnhandledInput fires regardless of Visible, so F8 can
        // bring the overlay back after the close button hid it.
        if (@event is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.F8)
        {
            _userVisible = !_userVisible;
            UpdateEffectiveVisibility();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        bool hide = false;
        try
        {
            var cap = NCapstoneContainer.Instance;
            if (cap != null && cap.InUse) hide = true;
        }
        catch { }
        if (_menuHiding != hide)
        {
            _menuHiding = hide;
            UpdateEffectiveVisibility();
        }

        if (_breakdownPopup != null && IsInstanceValid(_breakdownPopup))
            RepositionBreakdown();
    }

    private void UpdateEffectiveVisibility()
    {
        Visible = _userVisible && !_menuHiding;
    }

    private void BuildUi()
    {
        var bg = new Panel { MouseFilter = MouseFilterEnum.Stop };
        bg.AddThemeStyleboxOverride("panel", MakeStyleBox(
            BgColor, 6, BorderColor, 1));
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var vbox = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        vbox.SetAnchorsPreset(LayoutPreset.FullRect);
        vbox.OffsetLeft = PadLarge;
        vbox.OffsetTop = 4;
        vbox.OffsetRight = -(PadLarge);
        vbox.OffsetBottom = -Pad;
        vbox.AddThemeConstantOverride("separation", 3);
        AddChild(vbox);

        vbox.AddChild(BuildTitleBar());
        _summaryRow = BuildSummaryRow();
        vbox.AddChild(_summaryRow);
        _scopeTabs = BuildScopeTabs();
        vbox.AddChild(_scopeTabs);
        _metricSelector = BuildMetricSelector();
        vbox.AddChild(_metricSelector);
        _aggRow = BuildAggToggle();
        vbox.AddChild(_aggRow);

        var sep = new ColorRect
        {
            Color = BorderColor,
            CustomMinimumSize = new Vector2(0, 1),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        vbox.AddChild(sep);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            MouseFilter = MouseFilterEnum.Pass,
        };
        vbox.AddChild(scroll);

        _rowBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _rowBox.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_rowBox);

        AddChild(BuildResizeHandle(ResizeEdge.BottomRight));
        AddChild(BuildResizeHandle(ResizeEdge.BottomLeft));
        AddChild(BuildResizeHandle(ResizeEdge.Bottom));
        UpdateScopeHighlights();
        UpdateAggButton();
        UpdateFilterCollapse();
    }

    private Control BuildSummaryRow()
    {
        var panel = new Panel
        {
            CustomMinimumSize = new Vector2(0, 22),
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.PointingHand,
        };
        panel.AddThemeStyleboxOverride("panel", MakeStyleBox(TabInactiveColor, 3));
        panel.GuiInput += e =>
        {
            if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                ToggleFilterCollapse();
                GetViewport().SetInputAsHandled();
            }
        };

        var hbox = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        hbox.SetAnchorsPreset(LayoutPreset.FullRect);
        hbox.OffsetLeft = 8;
        hbox.OffsetRight = -8;
        hbox.AddThemeConstantOverride("separation", 4);
        panel.AddChild(hbox);

        _summaryLabel = new RichTextLabel
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _summaryLabel.AddThemeFontSizeOverride("normal_font_size", 12);
        hbox.AddChild(_summaryLabel);

        _summaryChevron = new Label
        {
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _summaryChevron.AddThemeFontSizeOverride("font_size", 12);
        _summaryChevron.AddThemeColorOverride("font_color", SeparatorColor);
        hbox.AddChild(_summaryChevron);

        return panel;
    }

    private void ToggleFilterCollapse()
    {
        float oldHeader = CurrentHeaderHeight;
        _filtersCollapsed = !_filtersCollapsed;
        float delta = CurrentHeaderHeight - oldHeader;
        if (_userResized)
            Size = new Vector2(Size.X, Size.Y + delta);
        UpdateFilterCollapse();
        Refresh();
    }

    private void UpdateFilterCollapse()
    {
        // Summary row stays visible in both states so the chevron is always
        // available as the collapse/expand affordance.
        _scopeTabs.Visible = !_filtersCollapsed;
        _metricSelector.Visible = !_filtersCollapsed;
        _aggRow.Visible = !_filtersCollapsed;
        string sep = $"[color={ToHex(SeparatorColor)}]\u00B7[/color]";
        _summaryLabel.Text =
            $"[color={ToHex(AccentColor)}]{ScopeNames[(int)_scope]}[/color] {sep} " +
            $"[color={ToHex(MetricAccentColors[(int)_metric])}]{MetricNames[(int)_metric]}[/color] {sep} " +
            $"[color={ToHex(AggAccentColor)}]{(_perTurn ? "Per Turn" : "Cumulative")}[/color]";
        _summaryChevron.Text = _filtersCollapsed ? "\u25B8" : "\u25BE";
    }

    private Control BuildTitleBar()
    {
        var panel = new Panel
        {
            CustomMinimumSize = new Vector2(0, TitleH),
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.Move,
        };
        var style = MakeStyleBox(TitleColor, 6);
        style.CornerRadiusBottomLeft = 0;
        style.CornerRadiusBottomRight = 0;
        panel.AddThemeStyleboxOverride("panel", style);
        panel.GuiInput += HandleDragInput;

        var hbox = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        hbox.SetAnchorsPreset(LayoutPreset.FullRect);
        hbox.OffsetLeft = 8;
        hbox.OffsetRight = -4;
        hbox.AddThemeConstantOverride("separation", 4);
        panel.AddChild(hbox);

        var title = new Label
        {
            Text = "Stats Tracker",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        title.AddThemeFontSizeOverride("font_size", 13);
        title.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.9f));
        hbox.AddChild(title);

        var closeBtn = new Button
        {
            Text = "\u2715",
            TooltipText = "Hide (F8 to toggle)",
            CustomMinimumSize = new Vector2(20, 18),
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        closeBtn.AddThemeFontSizeOverride("font_size", 11);
        StyleButton(closeBtn, Colors.Transparent, new Color(0.5f, 0.15f, 0.15f, 0.8f));
        closeBtn.Pressed += () => { _userVisible = false; UpdateEffectiveVisibility(); };
        hbox.AddChild(closeBtn);

        return panel;
    }

    private HBoxContainer BuildScopeTabs()
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 2);
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var btn = new Button
            {
                Text = ScopeNames[i],
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 24),
                FocusMode = FocusModeEnum.None,
                MouseFilter = MouseFilterEnum.Stop,
            };
            btn.AddThemeFontSizeOverride("font_size", 12);
            btn.Pressed += () => SetScope((DmScope)idx);
            row.AddChild(btn);
            _scopeBtns[i] = btn;
        }
        return row;
    }

    private HBoxContainer BuildMetricSelector()
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 4);

        var prev = MakeArrowBtn("\u25C0");
        prev.Pressed += () => CycleMetric(-1);
        row.AddChild(prev);

        _metricLabel = new Label
        {
            Text = MetricNames[0],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _metricLabel.AddThemeFontSizeOverride("font_size", 13);
        _metricLabel.AddThemeColorOverride("font_color", MetricAccentColors[(int)_metric]);
        row.AddChild(_metricLabel);

        var next = MakeArrowBtn("\u25B6");
        next.Pressed += () => CycleMetric(1);
        row.AddChild(next);

        return row;
    }

    private Control BuildAggToggle()
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 2);

        _perTurnBtn = MakeAggBtn("Per Turn");
        _perTurnBtn.Pressed += () => SetAggregation(true);
        row.AddChild(_perTurnBtn);

        _cumulativeBtn = MakeAggBtn("Cumulative");
        _cumulativeBtn.Pressed += () => SetAggregation(false);
        row.AddChild(_cumulativeBtn);

        return row;
    }

    private static Button MakeAggBtn(string label)
    {
        var btn = new Button
        {
            Text = label,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 22),
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop,
        };
        btn.AddThemeFontSizeOverride("font_size", 11);
        return btn;
    }

    private Control BuildResizeHandle(ResizeEdge edge)
    {
        // Corner handles are 12x12 colored squares; the bottom edge is a thin
        // transparent strip between them so the user can grab anywhere along
        // the bottom without blocking scroll within the row area above.
        var tint = edge == ResizeEdge.Bottom
            ? new Color(0f, 0f, 0f, 0f)
            : new Color(0.5f, 0.5f, 0.6f, 0.3f);
        var handle = new ColorRect
        {
            Color = tint,
            MouseFilter = MouseFilterEnum.Stop,
        };

        switch (edge)
        {
            case ResizeEdge.BottomRight:
                handle.AnchorLeft = 1; handle.AnchorTop = 1;
                handle.AnchorRight = 1; handle.AnchorBottom = 1;
                handle.OffsetLeft = -5; handle.OffsetTop = -5;
                handle.MouseDefaultCursorShape = CursorShape.Fdiagsize;
                break;
            case ResizeEdge.BottomLeft:
                handle.AnchorLeft = 0; handle.AnchorTop = 1;
                handle.AnchorRight = 0; handle.AnchorBottom = 1;
                handle.OffsetRight = 5; handle.OffsetTop = -5;
                handle.MouseDefaultCursorShape = CursorShape.Bdiagsize;
                break;
            case ResizeEdge.Bottom:
                handle.AnchorLeft = 0; handle.AnchorTop = 1;
                handle.AnchorRight = 1; handle.AnchorBottom = 1;
                handle.OffsetLeft = 5; handle.OffsetRight = -5;
                handle.OffsetTop = -5;
                handle.MouseDefaultCursorShape = CursorShape.Vsize;
                break;
        }

        handle.GuiInput += e => HandleResizeInput(e, edge);
        return handle;
    }

    public void Refresh()
    {
        if (!IsInstanceValid(_rowBox)) return;
        HideBreakdown();
        foreach (var child in _rowBox.GetChildren())
            child.QueueFree();

        var state = RunManager.Instance?.DebugOnlyGetState();
        var scope = StatsTrackerData.ForScope(_scope);
        int turns = Math.Max(1, scope.PlayerTurns);

        // Only surface names in multiplayer — in singleplayer there's exactly one
        // bar and the name is just your own steam handle, which adds noise.
        bool showNames = RunManager.Instance?.NetService?.Type.IsMultiplayer() ?? false;

        var entries = new List<(float value, Color color, Texture2D? icon, string name, Dictionary<string, long>? breakdown)>();
        if (state?.Players != null)
        {
            foreach (var player in state.Players)
            {
                if (player == null) continue;
                scope.ByPlayer.TryGetValue(player.NetId, out var stats);
                long raw = GetMetric(stats);
                float val = _perTurn ? (float)raw / turns : raw;
                var character = player.Character;
                var color = character != null ? character.MapDrawingColor : new Color(0.6f, 0.6f, 0.6f);
                var icon = character?.IconTexture;
                var bd = GetBreakdown(stats);
                entries.Add((val, color, icon, showNames ? ResolvePlayerName(player.NetId) : "", bd));
            }
        }

        // Include any netIds the tracker saw that aren't in live Players (dead/despawned)
        // or the special Unattributed bucket.
        foreach (var kv in scope.ByPlayer)
        {
            if (kv.Key == StatsTrackerData.UnattributedId)
            {
                long raw = GetMetric(kv.Value);
                if (raw > 0)
                {
                    float val = _perTurn ? (float)raw / turns : raw;
                    var bd = GetBreakdown(kv.Value);
                    entries.Add((val, new Color(0.55f, 0.55f, 0.55f), null, "Unattributed", bd));
                }
                continue;
            }
            bool present = false;
            if (state?.Players != null)
            {
                foreach (var p in state.Players)
                    if (p != null && p.NetId == kv.Key) { present = true; break; }
            }
            if (present) continue;
            long raw2 = GetMetric(kv.Value);
            float val2 = _perTurn ? (float)raw2 / turns : raw2;
            var bd2 = GetBreakdown(kv.Value);
            entries.Add((val2, new Color(0.6f, 0.6f, 0.6f), null, showNames ? ResolvePlayerName(kv.Key) : "", bd2));
        }

        entries.Sort((a, b) => b.value.CompareTo(a.value));

        float max = 0f;
        foreach (var e in entries) if (e.value > max) max = e.value;

        foreach (var e in entries)
            _rowBox.AddChild(MakeRow(e.value, max, e.color, e.icon, e.name, e.breakdown));

        int n = Math.Max(1, entries.Count);
        _contentHeight = CurrentHeaderHeight + n * RowStrideH + Pad;
        if (!_userResized)
            Size = new Vector2(Size.X, _contentHeight);

        UpdateFilterCollapse();
    }

    // Resolve a display name for a player's netId. In multiplayer the netId IS
    // the Steam ID, so PlatformUtil resolves it directly. In singleplayer
    // NetSingleplayerGameService hardcodes NetId to 1, which the Steam strategy
    // can't look up (returns "1") — so detect that case and ask for the local
    // player's actual Steam ID instead.
    private static string ResolvePlayerName(ulong netId)
    {
        try
        {
            var net = RunManager.Instance?.NetService;
            if (net != null)
            {
                ulong lookupId = netId;
                if (net.Type == NetGameType.Singleplayer && netId == NetSingleplayerGameService.defaultNetId)
                    lookupId = PlatformUtil.GetLocalPlayerId(net.Platform);
                var name = PlatformUtil.GetPlayerName(net.Platform, lookupId);
                if (!string.IsNullOrEmpty(name) && name != lookupId.ToString()) return name;
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"StatsTracker name lookup: {e.Message}"); }
        return "P" + netId;
    }

    private long GetMetric(DmPlayerStats? stats)
    {
        if (stats == null) return 0;
        return _metric switch
        {
            DmMetric.DamageDealt => stats.DamageDealt,
            DmMetric.BlockGained => stats.BlockGained,
            DmMetric.HpLost => stats.HpLost,
            _ => 0,
        };
    }

    private Dictionary<string, long>? GetBreakdown(DmPlayerStats? stats)
    {
        if (stats == null) return null;
        return _metric switch
        {
            DmMetric.DamageDealt => stats.DamageBySource,
            DmMetric.BlockGained => stats.BlockBySource,
            DmMetric.HpLost => stats.HpLostBySource,
            _ => null,
        };
    }

    private static string FormatValue(float v)
    {
        if (v >= 1000f) return (v / 1000f).ToString("0.0") + "k";
        return ((int)Math.Round(v)).ToString();
    }

    internal Control MakeRow(float value, float maxValue, Color barColor, Texture2D? iconTex, string playerName, Dictionary<string, long>? breakdown)
    {
        var row = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0, RowH),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        row.AddThemeConstantOverride("separation", 4);

        Control icon;
        if (iconTex != null)
        {
            icon = new TextureRect
            {
                Texture = iconTex,
                CustomMinimumSize = new Vector2(18, 18),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Ignore,
            };
        }
        else
        {
            icon = new ColorRect
            {
                CustomMinimumSize = new Vector2(18, 18),
                Color = barColor,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Ignore,
            };
        }
        row.AddChild(icon);

        float pct = maxValue > 0 ? value / maxValue : 0f;
        var barBg = new Panel
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.Fill,
            MouseFilter = breakdown != null && breakdown.Count > 0
                ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore,
        };
        barBg.AddThemeStyleboxOverride("panel", MakeStyleBox(BarBgColor, 3));
        row.AddChild(barBg);

        if (breakdown != null && breakdown.Count > 0)
        {
            var bd = breakdown;
            barBg.MouseEntered += () => ShowBreakdown(bd, barBg);
            barBg.MouseExited += HideBreakdown;
        }

        var barFill = new ColorRect
        {
            Color = new Color(barColor.R, barColor.G, barColor.B, 0.75f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        barFill.AnchorRight = pct;
        barFill.AnchorBottom = 1f;
        barBg.AddChild(barFill);

        // Name on the left and value on the right both stretch across the full
        // bar width with opposite alignments — ClipContents on the Panel keeps
        // a long name from spilling past the bar, and the right-aligned value
        // stays readable since its text naturally hugs the right edge first.
        // Empty playerName = singleplayer, where the label would just be noise.
        if (!string.IsNullOrEmpty(playerName))
        {
            var nameLabel = new Label
            {
                Text = playerName,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
                ClipText = true,
            };
            nameLabel.SetAnchorsPreset(LayoutPreset.FullRect);
            nameLabel.OffsetLeft = 6;
            nameLabel.OffsetRight = -4;
            nameLabel.AddThemeFontSizeOverride("font_size", 12);
            nameLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
            nameLabel.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.9f));
            nameLabel.AddThemeConstantOverride("shadow_offset_x", 1);
            nameLabel.AddThemeConstantOverride("shadow_offset_y", 1);
            nameLabel.AddThemeConstantOverride("shadow_outline_size", 2);
            barBg.AddChild(nameLabel);
        }

        // Value overlays the right end of the bar. Shadow keeps the digits
        // readable whether they land over the filled portion or the empty track.
        var valLabel = new Label
        {
            Text = FormatValue(value),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        valLabel.SetAnchorsPreset(LayoutPreset.FullRect);
        valLabel.OffsetLeft = 4;
        valLabel.OffsetRight = -6;
        valLabel.AddThemeFontSizeOverride("font_size", 13);
        valLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
        valLabel.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.9f));
        valLabel.AddThemeConstantOverride("shadow_offset_x", 1);
        valLabel.AddThemeConstantOverride("shadow_offset_y", 1);
        valLabel.AddThemeConstantOverride("shadow_outline_size", 2);
        barBg.AddChild(valLabel);

        return row;
    }

    private const float BreakdownIconSize = 16f;

    // Anchor bar saved by ShowBreakdown so _Process can reposition the popup
    // to track the mouse while hovering.
    private Control? _breakdownAnchor;

    private void ShowBreakdown(Dictionary<string, long> breakdown, Control anchor)
    {
        HideBreakdown();

        var scope = StatsTrackerData.ForScope(_scope);
        int turns = Math.Max(1, scope.PlayerTurns);
        var accentColor = MetricAccentColors[(int)_metric];

        var sorted = breakdown.Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .ToList();
        if (sorted.Count == 0) return;

        long total = breakdown.Values.Sum(v => Math.Max(0, v));
        if (total <= 0) return;

        const float popupW = 280f;
        const float rowH = 20f;
        const float headerH = 24f;
        const float padX = 8f;
        const float padY = 6f;
        const float iconColW = BreakdownIconSize + 4f; // icon + gap
        float popupH = headerH + sorted.Count * rowH + padY * 2;

        var popup = new Control
        {
            Size = new Vector2(popupW, popupH),
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 10,
        };

        var bg = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        bg.AddThemeStyleboxOverride("panel", MakeStyleBox(BgColor, 4, BorderColor, 1));
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        popup.AddChild(bg);

        var header = new Label
        {
            Text = MetricNames[(int)_metric],
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        header.Position = new Vector2(padX, padY);
        header.Size = new Vector2(popupW - padX * 2, headerH);
        header.AddThemeFontSizeOverride("font_size", 12);
        header.AddThemeColorOverride("font_color", accentColor);
        popup.AddChild(header);

        // Each row: icon (optional) + backdrop bar + source bar (proportional
        // to total) + text overlay with source name left, value+% right.
        float contentX = padX;
        float barX = contentX + iconColW;
        float barW = popupW - padX * 2 - iconColW;
        float y = padY + headerH;
        foreach (var kv in sorted)
        {
            var resolved = SourceIconResolver.Resolve(kv.Key);
            float displayVal = _perTurn ? (float)kv.Value / turns : kv.Value;
            float pct = total > 0 ? (float)kv.Value / total * 100f : 0f;
            float barPct = total > 0 ? (float)kv.Value / total : 0f;
            var barColor = resolved.BarColor;

            // Icon column — wrap in a clipping container so overflow from
            // scaled-up padded icons gets cropped. Icons with built-in
            // transparent padding (atlas sprites, category icons) use
            // IconScale > 1 to render the TextureRect larger than the clip
            // box, making the visible content fill the space.
            if (resolved.Icon != null)
            {
                var iconClip = new Control
                {
                    Position = new Vector2(contentX, y + (rowH - BreakdownIconSize) * 0.5f),
                    Size = new Vector2(BreakdownIconSize, BreakdownIconSize),
                    ClipContents = true,
                    MouseFilter = MouseFilterEnum.Ignore,
                };
                var iconRect = new TextureRect
                {
                    Texture = resolved.Icon,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = MouseFilterEnum.Ignore,
                };
                iconRect.SetAnchorsPreset(LayoutPreset.FullRect);
                float scale = resolved.IconScale > 0f ? resolved.IconScale : 1f;
                if (scale > 1f)
                {
                    float grow = BreakdownIconSize * (scale - 1f) * 0.5f;
                    iconRect.OffsetLeft = -grow;
                    iconRect.OffsetTop = -grow;
                    iconRect.OffsetRight = grow;
                    iconRect.OffsetBottom = grow;
                }
                iconClip.AddChild(iconRect);
                popup.AddChild(iconClip);
            }

            // Layer 1: backdrop bar (full row width)
            var backdrop = new ColorRect
            {
                Color = BarBgColor,
                Position = new Vector2(barX, y),
                Size = new Vector2(barW, rowH),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            popup.AddChild(backdrop);

            // Layer 2: source bar (proportional to total)
            if (barPct > 0)
            {
                var barFill = new ColorRect
                {
                    Color = new Color(barColor.R, barColor.G, barColor.B, 0.55f),
                    Position = new Vector2(barX, y),
                    Size = new Vector2(barW * barPct, rowH),
                    MouseFilter = MouseFilterEnum.Ignore,
                };
                popup.AddChild(barFill);
            }

            // Layer 3: text overlay
            var nameLabel = new Label
            {
                Text = kv.Key,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                ClipText = true,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            nameLabel.Position = new Vector2(barX + 4, y);
            nameLabel.Size = new Vector2(barW * 0.55f, rowH);
            nameLabel.AddThemeFontSizeOverride("font_size", 11);
            nameLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.95f));
            nameLabel.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.9f));
            nameLabel.AddThemeConstantOverride("shadow_offset_x", 1);
            nameLabel.AddThemeConstantOverride("shadow_offset_y", 1);
            nameLabel.AddThemeConstantOverride("shadow_outline_size", 3);
            popup.AddChild(nameLabel);

            string valText = FormatValue(displayVal) + $" ({pct:0}%)";
            var valLabel = new Label
            {
                Text = valText,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            valLabel.Position = new Vector2(barX, y);
            valLabel.Size = new Vector2(barW - 4, rowH);
            valLabel.AddThemeFontSizeOverride("font_size", 11);
            valLabel.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.95f));
            valLabel.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.9f));
            valLabel.AddThemeConstantOverride("shadow_offset_x", 1);
            valLabel.AddThemeConstantOverride("shadow_offset_y", 1);
            valLabel.AddThemeConstantOverride("shadow_outline_size", 3);
            popup.AddChild(valLabel);

            y += rowH;
        }

        _breakdownPopup = popup;
        _breakdownAnchor = anchor;
        var mountParent = GetParent() as Control ?? this;
        mountParent.AddChild(popup);

        RepositionBreakdown();
    }

    private void RepositionBreakdown()
    {
        if (_breakdownPopup == null || !IsInstanceValid(_breakdownPopup)) return;
        if (_breakdownAnchor == null || !IsInstanceValid(_breakdownAnchor)) return;

        var mousePos = GetViewport()?.GetMousePosition() ?? _breakdownAnchor.GlobalPosition;
        var vpSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
        float popupW = _breakdownPopup.Size.X;
        float popupH = _breakdownPopup.Size.Y;

        // Left of mouse, below the bar
        float popupX = mousePos.X - popupW;
        float popupY = _breakdownAnchor.GlobalPosition.Y + _breakdownAnchor.Size.Y + 2f;

        if (popupX < 0) popupX = mousePos.X;
        if (popupY + popupH > vpSize.Y) popupY = _breakdownAnchor.GlobalPosition.Y - popupH - 2f;
        if (popupY < 0) popupY = 0;

        _breakdownPopup.GlobalPosition = new Vector2(popupX, popupY);
    }

    private void HideBreakdown()
    {
        if (_breakdownPopup != null && IsInstanceValid(_breakdownPopup))
            _breakdownPopup.QueueFree();
        _breakdownPopup = null;
        _breakdownAnchor = null;
    }

    private void HandleDragInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            _dragging = mb.Pressed;
            if (mb.Pressed) _dragOffset = mb.GlobalPosition - Position;
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventMouseMotion mm && _dragging)
        {
            Position = mm.GlobalPosition - _dragOffset;
            GetViewport().SetInputAsHandled();
        }
    }

    private void HandleResizeInput(InputEvent @event, ResizeEdge edge)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _resizing = true;
                _resizeEdge = edge;
                _resizeStart = mb.GlobalPosition;
                _sizeStart = Size;
                _positionStart = Position;
            }
            else _resizing = false;
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventMouseMotion mm && _resizing)
        {
            var delta = mm.GlobalPosition - _resizeStart;
            float newW = _sizeStart.X;
            float newH = _sizeStart.Y;
            float newX = _positionStart.X;

            switch (_resizeEdge)
            {
                case ResizeEdge.BottomRight:
                    newW = Mathf.Max(MinW, _sizeStart.X + delta.X);
                    newH = Mathf.Max(_contentHeight, _sizeStart.Y + delta.Y);
                    break;
                case ResizeEdge.Bottom:
                    newH = Mathf.Max(_contentHeight, _sizeStart.Y + delta.Y);
                    break;
                case ResizeEdge.BottomLeft:
                    newW = Mathf.Max(MinW, _sizeStart.X - delta.X);
                    newH = Mathf.Max(_contentHeight, _sizeStart.Y + delta.Y);
                    // Shift X so the right edge stays pinned while the left drags.
                    newX = _positionStart.X + (_sizeStart.X - newW);
                    break;
            }

            Position = new Vector2(newX, _positionStart.Y);
            Size = new Vector2(newW, newH);
            _userResized = true; // stop auto-fitting height on Refresh
            GetViewport().SetInputAsHandled();
        }
    }

    private void SetScope(DmScope scope)
    {
        _scope = scope;
        UpdateScopeHighlights();
        Refresh();
    }

    private void CycleMetric(int dir)
    {
        int count = MetricNames.Length;
        _metric = (DmMetric)(((int)_metric + dir + count) % count);
        _metricLabel.Text = MetricNames[(int)_metric];
        _metricLabel.AddThemeColorOverride("font_color", MetricAccentColors[(int)_metric]);
        Refresh();
    }

    private void SetAggregation(bool perTurn)
    {
        if (_perTurn == perTurn) return;
        _perTurn = perTurn;
        UpdateAggButton();
        Refresh();
    }

    private void UpdateScopeHighlights()
    {
        for (int i = 0; i < 3; i++)
            StyleTabButton(_scopeBtns[i], i == (int)_scope, AccentColor);
    }

    private static void StyleTabButton(Button btn, bool active, Color accent)
    {
        var bg = new Color(0.14f, 0.14f, 0.20f, 0.8f);
        var hoverBg = new Color(0.22f, 0.22f, 0.30f, 0.9f);
        btn.AddThemeStyleboxOverride("normal", MakeTabStylebox(bg, active, accent));
        btn.AddThemeStyleboxOverride("hover", MakeTabStylebox(hoverBg, active, accent));
        btn.AddThemeStyleboxOverride("pressed", MakeTabStylebox(hoverBg, active, accent));
        btn.AddThemeStyleboxOverride("focus", MakeStyleBox(Colors.Transparent));

        var inactiveText = new Color(0.62f, 0.62f, 0.70f);
        btn.AddThemeColorOverride("font_color", active ? accent : inactiveText);
        btn.AddThemeColorOverride("font_hover_color", active ? accent : new Color(0.85f, 0.85f, 0.92f));
        btn.AddThemeColorOverride("font_pressed_color", active ? accent : inactiveText);
    }

    private static StyleBoxFlat MakeTabStylebox(Color bg, bool active, Color accent)
    {
        var sb = new StyleBoxFlat { BgColor = bg };
        sb.CornerRadiusTopLeft = 3;
        sb.CornerRadiusTopRight = 3;
        sb.CornerRadiusBottomLeft = 3;
        sb.CornerRadiusBottomRight = 3;
        if (active)
        {
            sb.BorderColor = accent;
            sb.BorderWidthBottom = 2;
        }
        return sb;
    }

    private void UpdateAggButton()
    {
        StyleTabButton(_perTurnBtn, _perTurn, AggAccentColor);
        StyleTabButton(_cumulativeBtn, !_perTurn, AggAccentColor);
    }

    private static Button MakeArrowBtn(string text)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(28, 24),
            FocusMode = FocusModeEnum.None,
            MouseFilter = MouseFilterEnum.Stop,
        };
        btn.AddThemeFontSizeOverride("font_size", 11);
        StyleButton(btn, TabInactiveColor, new Color(0.2f, 0.2f, 0.28f, 0.8f));
        return btn;
    }

    private static void StyleButton(Button btn, Color normal, Color hover)
    {
        btn.AddThemeStyleboxOverride("normal", MakeStyleBox(normal, 3));
        btn.AddThemeStyleboxOverride("hover", MakeStyleBox(hover, 3));
        btn.AddThemeStyleboxOverride("pressed", MakeStyleBox(hover, 3));
        btn.AddThemeStyleboxOverride("focus", MakeStyleBox(Colors.Transparent));
    }

    private static string ToHex(Color c) =>
        $"#{(byte)Mathf.Clamp(c.R * 255f, 0, 255):X2}{(byte)Mathf.Clamp(c.G * 255f, 0, 255):X2}{(byte)Mathf.Clamp(c.B * 255f, 0, 255):X2}";

    private static StyleBoxFlat MakeStyleBox(Color bg, int corner = 0, Color? border = null, int borderW = 0)
    {
        var sb = new StyleBoxFlat { BgColor = bg };
        if (corner > 0)
        {
            sb.CornerRadiusTopLeft = corner;
            sb.CornerRadiusTopRight = corner;
            sb.CornerRadiusBottomLeft = corner;
            sb.CornerRadiusBottomRight = corner;
        }
        if (border.HasValue && borderW > 0)
        {
            sb.BorderColor = border.Value;
            sb.BorderWidthTop = borderW;
            sb.BorderWidthBottom = borderW;
            sb.BorderWidthLeft = borderW;
            sb.BorderWidthRight = borderW;
        }
        return sb;
    }
}
