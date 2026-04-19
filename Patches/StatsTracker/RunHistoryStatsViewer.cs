using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

using dubiousQOL.UI;

namespace dubiousQOL.Patches;

internal static class RunHistoryStatsViewerModal
{
    public static void Open(DmSidecar sidecar)
    {
        var modal = NModalContainer.Instance;
        if (modal == null) return;

        if (sidecar.Scopes == null && (sidecar.Combats == null || sidecar.Combats.Count == 0))
        {
            modal.Add(ErrorPanel("No detailed stats available for this run."));
            return;
        }

        try { modal.Add(new RunHistoryStatsViewer(sidecar), showBackstop: true); }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"RunHistoryStats open: {e.Message}\n{e.StackTrace}");
            modal.Add(ErrorPanel("Failed to open stats viewer (see logs)."));
        }
    }

    private static Control ErrorPanel(string msg)
    {
        var root = new Control { Name = "DubiousStatsError", MouseFilter = Control.MouseFilterEnum.Stop };
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

internal partial class RunHistoryStatsViewer : Control, IScreenContext
{
    // Panel styling
    private static readonly Color PanelColor = new(0.12f, 0.14f, 0.18f, 0.92f);
    private static readonly Color SectionHeaderColor = new(0.95f, 0.80f, 0.30f);
    private static readonly Color EncounterHeaderColor = new(0.85f, 0.70f, 0.30f);
    private static readonly Color StatLabelColor = new(0.90f, 0.90f, 0.85f);
    private static readonly Color StatValueColor = new(0.70f, 0.85f, 0.70f);
    private static readonly Color DimTextColor = new(0.60f, 0.60f, 0.55f);
    private static readonly Color DamageColor = new(0.85f, 0.35f, 0.35f);
    private static readonly Color BlockColor = new(0.35f, 0.65f, 0.90f);
    private static readonly Color HpLostColor = new(0.72f, 0.42f, 0.95f);
    private const int MaxSourceRows = 10;

    public Control? DefaultFocusedControl => null;

    private readonly DmSidecar _sidecar;
    private readonly List<int> _actIndices;
    private ScrollContainer _scroll = null!;
    private readonly List<Control> _pages = new();
    private readonly List<Node> _tabs = new();
    private int _activeTab;

    // Source tab node for cloning — grabbed from a temporary stats screen scene.
    private Node? _sourceTab;

    public RunHistoryStatsViewer(DmSidecar sidecar)
    {
        _sidecar = sidecar;
        Name = "DubiousRunHistoryStats";
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.FullRect);

        // Determine which acts have combat data.
        _actIndices = new List<int>();
        if (sidecar.Combats != null)
        {
            foreach (var c in sidecar.Combats)
            {
                if (!_actIndices.Contains(c.Act))
                    _actIndices.Add(c.Act);
            }
            _actIndices.Sort();
        }

        AcquireSourceTab();
        Build();
    }

    public override void _Ready()
    {
        // Hide the run history screen behind us (same pattern as ModConfigUI).
        try
        {
            var runHistory = UiHelper.FindFirst<NRunHistory>(GetTree().Root);
            if (runHistory != null)
            {
                runHistory.Visible = false;
                var screenRef = runHistory;
                TreeExiting += () =>
                {
                    if (GodotObject.IsInstanceValid(screenRef))
                        screenRef.Visible = true;
                };
            }
        }
        catch { }

        // Back button must be added after the viewer is in the tree so the
        // clone's _Ready fires and initializes internal fields before Enable().
        try
        {
            var backBtn = PreloadManager.Cache.GetScene(
                SceneHelper.GetScenePath("ui/back_button")
            ).Instantiate<NBackButton>(PackedScene.GenEditState.Disabled);
            backBtn.Name = "DubiousStatsBackButton";
            backBtn.Released += _ => NModalContainer.Instance?.Clear();
            AddChild(backBtn);
            backBtn.CallDeferred(NClickableControl.MethodName.Enable);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"RunHistoryStats back button: {e.Message}");
            var close = new Button { Text = "Close", CustomMinimumSize = new Vector2(140, 44) };
            close.AnchorLeft = 0f; close.AnchorRight = 0f;
            close.AnchorTop = 1f; close.AnchorBottom = 1f;
            close.OffsetLeft = 24; close.OffsetRight = 164;
            close.OffsetTop = -84; close.OffsetBottom = -40;
            close.Pressed += () => NModalContainer.Instance?.Clear();
            AddChild(close);
        }
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true } k && k.Keycode == Key.Escape)
        {
            NModalContainer.Instance?.Clear();
            GetViewport().SetInputAsHandled();
        }
    }

    // Grab a tab node from the game's settings or stats screen for cloning.
    private void AcquireSourceTab()
    {
        try
        {
            // Try the settings screen scene — the same source ModConfigUI uses.
            var settingsPath = SceneHelper.GetScenePath("screens/settings_screen/settings_screen");
            if (TryExtractTab(settingsPath, "SettingsTabManager/General")) return;

            // Fallback: try the stats screen scene.
            var statsPath = SceneHelper.GetScenePath("screens/stats_screen/stats_screen");
            if (TryExtractTab(statsPath, "Tabs")) return;
        }
        catch (Exception e) { MainFile.Logger.Warn($"RunHistoryStats tab acquire: {e.Message}"); }
    }

    private bool TryExtractTab(string scenePath, string tabManagerPath)
    {
        try
        {
            if (!ResourceLoader.Exists(scenePath)) return false;
            var scene = PreloadManager.Cache.GetScene(scenePath);
            var tmp = scene.Instantiate<Node>(PackedScene.GenEditState.Disabled);
            try
            {
                var tabMgr = tmp.GetNodeOrNull(tabManagerPath);
                if (tabMgr == null) return false;
                // Find any NSettingsTab child (could be direct or nested in a TabContainer).
                var tab = FindFirstTab(tabMgr);
                if (tab == null) return false;
                tab.GetParent()?.RemoveChild(tab);
                _sourceTab = tab;
                return true;
            }
            finally { tmp.QueueFree(); }
        }
        catch { return false; }
    }

    private static Node? FindFirstTab(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            // NSettingsTab extends NButton — check if it has the SetLabel method.
            if (child.HasMethod("SetLabel")) return child;
            var nested = FindFirstTab(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private void Build()
    {
        // Tab bar at top center.
        var tabBar = new HBoxContainer { Name = "TabBar" };
        tabBar.AddThemeConstantOverride("separation", 12);
        tabBar.AnchorLeft = 0.5f; tabBar.AnchorRight = 0.5f;
        tabBar.AnchorTop = 0f; tabBar.AnchorBottom = 0f;
        tabBar.OffsetLeft = -400; tabBar.OffsetRight = 400;
        tabBar.OffsetTop = 80; tabBar.OffsetBottom = 150;
        tabBar.Alignment = BoxContainer.AlignmentMode.Center;
        AddChild(tabBar);

        // Build tab names.
        var tabNames = new List<string> { "Run Summary" };
        foreach (var actIdx in _actIndices)
            tabNames.Add($"Act {actIdx + 1}");

        // Create tabs.
        for (int i = 0; i < tabNames.Count; i++)
        {
            var tab = CreateTab(tabNames[i]);
            tabBar.AddChild(tab);
            _tabs.Add(tab);

            int capturedIdx = i;
            bool isSelected = i == 0;

            // Wire initial state after _Ready.
            tab.Ready += () =>
            {
                var outline = tab.GetNodeOrNull<TextureRect>("Outline");
                if (outline != null) outline.Visible = isSelected;
                var lbl = tab.GetNodeOrNull<Control>("Label");
                if (lbl != null) lbl.Modulate = isSelected
                    ? new Color("FFF6E2")
                    : new Color("FFF6E280");
                tab.Set("_isSelected", isSelected);
            };

            // Wire click handler.
            if (tab is NClickableControl clickTab)
                clickTab.Released += _ => SwitchTab(capturedIdx);
            else
                tab.Connect("pressed", Callable.From(() => SwitchTab(capturedIdx)));
        }

        // Scrollable content area.
        _scroll = new ScrollContainer
        {
            Name = "StatsScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _scroll.AnchorLeft = 0f; _scroll.AnchorRight = 1f;
        _scroll.AnchorTop = 0f; _scroll.AnchorBottom = 1f;
        _scroll.OffsetLeft = 200; _scroll.OffsetRight = -200;
        _scroll.OffsetTop = 160; _scroll.OffsetBottom = -40;
        AddChild(_scroll);

        // Build pages.
        _pages.Add(BuildRunSummaryPage());
        foreach (var actIdx in _actIndices)
            _pages.Add(BuildActPage(actIdx));

        // Show the first page.
        if (_pages.Count > 0)
            _scroll.AddChild(_pages[0]);
        _activeTab = 0;
    }

    private Node CreateTab(string label)
    {
        if (_sourceTab != null)
        {
            var tab = _sourceTab.Duplicate(15);
            // Each tab needs its own shader material for independent HSV hover.
            var tabImg = tab.GetNodeOrNull<TextureRect>("TabImage");
            if (tabImg?.Material is ShaderMaterial sm)
                tabImg.Material = (Material)sm.Duplicate();
            tab.CallDeferred("SetLabel", label);
            return tab;
        }

        // Fallback: plain styled button.
        var btn = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(160, 56),
            FocusMode = FocusModeEnum.All,
        };
        btn.AddThemeFontSizeOverride("font_size", 22);
        return btn;
    }

    private void SwitchTab(int idx)
    {
        if (idx == _activeTab || idx < 0 || idx >= _pages.Count) return;
        _scroll.RemoveChild(_pages[_activeTab]);
        _scroll.AddChild(_pages[idx]);
        _scroll.ScrollVertical = 0;

        // Update tab visuals.
        if (_activeTab < _tabs.Count)
            _tabs[_activeTab].Call("Deselect");
        if (idx < _tabs.Count)
            _tabs[idx].Call("Select");

        _activeTab = idx;
    }

    // ─────────────────────────────────────────────────────────
    //  Run Summary page
    // ─────────────────────────────────────────────────────────

    private Control BuildRunSummaryPage()
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 24);
        vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        vbox.AddChild(CreateSpacer(8));

        DmSidecarScope? runScope = null;
        _sidecar.Scopes?.TryGetValue("run", out runScope);

        // --- Full Run Summary ---
        vbox.AddChild(CreateSectionHeader("Run Summary"));

        if (runScope != null && runScope.Players.Count > 0)
        {
            foreach (var player in runScope.Players)
            {
                if (runScope.Players.Count > 1)
                    vbox.AddChild(CreateEncounterHeader(player.Character ?? $"Player {player.NetId}", ""));

                // Summary chips row.
                var summaryRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
                summaryRow.AddThemeConstantOverride("separation", 32);
                summaryRow.AddChild(CreateStatChip("DMG", player.DamageDealt, DamageColor));
                summaryRow.AddChild(CreateStatChip("BLK", player.BlockGained, BlockColor));
                summaryRow.AddChild(CreateStatChip("HP\u2009Lost", player.HpLost, HpLostColor));
                summaryRow.AddChild(CreateStatChip("Turns", runScope.PlayerTurns, StatLabelColor));
                vbox.AddChild(summaryRow);

                // Damage breakdown.
                if (player.DamageBySource != null && player.DamageBySource.Count > 0)
                {
                    vbox.AddChild(CreateMiniSectionLabel($"Damage Sources \u2014 {player.DamageDealt:N0} total"));
                    vbox.AddChild(BuildBreakdownPanel(player.DamageBySource, player.DamageDealt, DamageColor));
                }

                // Block breakdown.
                if (player.BlockBySource != null && player.BlockBySource.Count > 0)
                {
                    vbox.AddChild(CreateMiniSectionLabel($"Block Sources \u2014 {player.BlockGained:N0} total"));
                    vbox.AddChild(BuildBreakdownPanel(player.BlockBySource, player.BlockGained, BlockColor));
                }

                // HP lost breakdown.
                if (player.HpLostBySource != null && player.HpLostBySource.Count > 0)
                {
                    vbox.AddChild(CreateMiniSectionLabel($"HP Lost Sources \u2014 {player.HpLost:N0} total"));
                    vbox.AddChild(BuildBreakdownPanel(player.HpLostBySource, player.HpLost, HpLostColor));
                }
            }
        }
        else
        {
            // V1 sidecar or no breakdown data.
            foreach (var player in _sidecar.Players)
            {
                var panel = CreateDarkPanel();
                var grid = new VBoxContainer();
                grid.AddThemeConstantOverride("separation", 8);
                panel.AddChild(grid);
                grid.AddChild(CreateStatRow("Damage Dealt", player.DamageDealt.ToString("N0"), DamageColor));
                grid.AddChild(CreateStatRow("Block Gained", player.BlockGained.ToString("N0"), BlockColor));
                grid.AddChild(CreateStatRow("HP Lost", player.HpLost.ToString("N0"), HpLostColor));
                vbox.AddChild(panel);
            }
        }

        // --- Per-Act Summaries ---
        if (_actIndices.Count > 0 && _sidecar.Combats != null)
        {
            foreach (var actIdx in _actIndices)
            {
                var actCombats = _sidecar.Combats.Where(c => c.Act == actIdx).ToList();
                if (actCombats.Count == 0) continue;

                vbox.AddChild(CreateSectionHeader($"Act {actIdx + 1}"));

                long actDmg = 0, actBlk = 0, actHp = 0;
                int actTurns = 0;
                foreach (var c in actCombats)
                {
                    actDmg += SumPlayerStat(c.Players, p => p.DamageDealt);
                    actBlk += SumPlayerStat(c.Players, p => p.BlockGained);
                    actHp += SumPlayerStat(c.Players, p => p.HpLost);
                    actTurns += c.PlayerTurns;
                }

                var actSummary = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
                actSummary.AddThemeConstantOverride("separation", 32);
                actSummary.AddChild(CreateStatChip("DMG", actDmg, DamageColor));
                actSummary.AddChild(CreateStatChip("BLK", actBlk, BlockColor));
                actSummary.AddChild(CreateStatChip("HP\u2009Lost", actHp, HpLostColor));
                actSummary.AddChild(CreateStatChip("Turns", actTurns, StatLabelColor));
                actSummary.AddChild(CreateStatChip("Encounters", actCombats.Count, DimTextColor));
                vbox.AddChild(actSummary);
            }
        }

        vbox.AddChild(CreateSpacer(24));
        return vbox;
    }

    // ─────────────────────────────────────────────────────────
    //  Act page — per-encounter journey
    // ─────────────────────────────────────────────────────────

    private Control BuildActPage(int actIndex)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 24);
        vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        vbox.AddChild(CreateSpacer(8));

        var combats = _sidecar.Combats?
            .Where(c => c.Act == actIndex)
            .OrderBy(c => c.Floor)
            .ToList();

        if (combats == null || combats.Count == 0)
        {
            var empty = CreateStatLabel("No encounters recorded for this act.", DimTextColor, 22);
            empty.CustomMinimumSize = new Vector2(0, 80);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            vbox.AddChild(empty);
            return vbox;
        }

        foreach (var combat in combats)
        {
            string roomTag = combat.RoomType ?? "Monster";
            string encounterName = ResolveEncounterName(combat.Encounter);
            string header = $"Floor {combat.Floor + 1} \u2014 {encounterName}";
            if (roomTag != "Monster")
                header += $" ({roomTag})";

            vbox.AddChild(CreateEncounterHeader(header, roomTag));

            var panel = CreateDarkPanel();
            var content = new VBoxContainer();
            content.AddThemeConstantOverride("separation", 10);
            panel.AddChild(content);

            // Summary line.
            var summaryRow = new HBoxContainer();
            summaryRow.AddThemeConstantOverride("separation", 32);
            summaryRow.AddChild(CreateStatChip("DMG", SumPlayerStat(combat.Players, p => p.DamageDealt), DamageColor));
            summaryRow.AddChild(CreateStatChip("BLK", SumPlayerStat(combat.Players, p => p.BlockGained), BlockColor));
            summaryRow.AddChild(CreateStatChip("HP\u2009Lost", SumPlayerStat(combat.Players, p => p.HpLost), HpLostColor));
            summaryRow.AddChild(CreateStatChip("Turns", combat.PlayerTurns, StatLabelColor));
            content.AddChild(summaryRow);

            // Source breakdowns for the first player (single-player).
            // For multiplayer, iterate all players.
            foreach (var player in combat.Players)
            {
                if (combat.Players.Count > 1)
                {
                    var playerLabel = CreateStatLabel(
                        player.Character ?? $"Player {player.NetId}",
                        EncounterHeaderColor, 20);
                    playerLabel.CustomMinimumSize = new Vector2(0, 28);
                    content.AddChild(playerLabel);
                }

                if (player.DamageBySource != null && player.DamageBySource.Count > 0)
                {
                    content.AddChild(CreateMiniSectionLabel("Damage Sources"));
                    content.AddChild(BuildBreakdownPanel(player.DamageBySource, player.DamageDealt, DamageColor, compact: true));
                }
                if (player.BlockBySource != null && player.BlockBySource.Count > 0)
                {
                    content.AddChild(CreateMiniSectionLabel("Block Sources"));
                    content.AddChild(BuildBreakdownPanel(player.BlockBySource, player.BlockGained, BlockColor, compact: true));
                }
                if (player.HpLostBySource != null && player.HpLostBySource.Count > 0)
                {
                    content.AddChild(CreateMiniSectionLabel("HP Lost Sources"));
                    content.AddChild(BuildBreakdownPanel(player.HpLostBySource, player.HpLost, HpLostColor, compact: true));
                }
            }

            vbox.AddChild(panel);
        }

        vbox.AddChild(CreateSpacer(24));
        return vbox;
    }

    // ─────────────────────────────────────────────────────────
    //  UI building helpers
    // ─────────────────────────────────────────────────────────

    private static Control BuildBreakdownPanel(Dictionary<string, long> sources, long total, Color accentColor, bool compact = false)
    {
        var panel = CreateDarkPanel();
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", compact ? 4 : 6);
        panel.AddChild(vbox);

        var sorted = sources.OrderByDescending(kv => kv.Value).ToList();
        int shown = Math.Min(sorted.Count, MaxSourceRows);

        for (int i = 0; i < shown; i++)
        {
            var kv = sorted[i];
            float pct = total > 0 ? (float)kv.Value / total : 0f;
            vbox.AddChild(CreateSourceRow(kv.Key, kv.Value, pct, accentColor, compact));
        }

        if (sorted.Count > MaxSourceRows)
        {
            long remaining = sorted.Skip(MaxSourceRows).Sum(kv => kv.Value);
            float pct = total > 0 ? (float)remaining / total : 0f;
            vbox.AddChild(CreateSourceRow($"... {sorted.Count - MaxSourceRows} more", remaining, pct, DimTextColor, compact));
        }

        return panel;
    }

    private static Control CreateSourceRow(string name, long value, float pct, Color accentColor, bool compact)
    {
        int rowHeight = compact ? 28 : 36;
        int fontSize = compact ? 18 : 22;
        int iconSize = compact ? 24 : 32;

        // Root container with the bar behind it.
        var root = new Control
        {
            CustomMinimumSize = new Vector2(0, rowHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };

        // Background percentage bar.
        var bar = new ColorRect
        {
            Color = new Color(accentColor.R, accentColor.G, accentColor.B, 0.18f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        bar.AnchorLeft = 0f; bar.AnchorRight = Math.Clamp(pct, 0.01f, 1f);
        bar.AnchorTop = 0f; bar.AnchorBottom = 1f;
        root.AddChild(bar);

        // Content row.
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 10);
        row.SetAnchorsPreset(LayoutPreset.FullRect);
        row.OffsetLeft = 8;
        row.OffsetRight = -8;
        root.AddChild(row);

        // Icon.
        Texture2D? icon = null;
        Color iconBarColor = accentColor;
        float iconScale = 1f;
        try
        {
            var resolved = SourceIconResolver.Resolve(name);
            icon = resolved.Icon;
            iconBarColor = resolved.BarColor;
            iconScale = resolved.IconScale;
        }
        catch { }

        if (icon != null)
        {
            // Clip container — icon scales larger than this box, padding overflows.
            var clip = new Control
            {
                CustomMinimumSize = new Vector2(iconSize, iconSize),
                ClipContents = true,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            clip.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            var texRect = new TextureRect
            {
                Texture = icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            float scaledSize = iconSize * iconScale;
            float offset = (iconSize - scaledSize) / 2f;
            texRect.Position = new Vector2(offset, offset);
            texRect.Size = new Vector2(scaledSize, scaledSize);
            clip.AddChild(texRect);
            row.AddChild(clip);
        }
        else
        {
            row.AddChild(CreateSpacer(iconSize, horizontal: true));
        }

        // Source name.
        var nameLabel = new Label
        {
            Text = name,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        nameLabel.AddThemeFontSizeOverride("font_size", fontSize);
        nameLabel.AddThemeColorOverride("font_color", StatLabelColor);
        row.AddChild(nameLabel);

        // Value.
        var valueLabel = new Label
        {
            Text = value.ToString("N0"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(100, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        valueLabel.AddThemeFontSizeOverride("font_size", fontSize);
        valueLabel.AddThemeColorOverride("font_color", StatValueColor);
        row.AddChild(valueLabel);

        // Percentage.
        var pctLabel = new Label
        {
            Text = $"{pct * 100:F0}%",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(52, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        pctLabel.AddThemeFontSizeOverride("font_size", fontSize - 2);
        pctLabel.AddThemeColorOverride("font_color", DimTextColor);
        row.AddChild(pctLabel);

        return root;
    }

    private static PanelContainer CreateDarkPanel()
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat
        {
            BgColor = PanelColor,
            CornerRadiusTopLeft = 12,
            CornerRadiusTopRight = 12,
            CornerRadiusBottomLeft = 12,
            CornerRadiusBottomRight = 12,
            ContentMarginLeft = 16,
            ContentMarginRight = 16,
            ContentMarginTop = 14,
            ContentMarginBottom = 14,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    private static Control CreateSectionHeader(string text)
    {
        var lbl = new MegaCrit.Sts2.addons.mega_text.MegaLabel();
        lbl.MouseFilter = MouseFilterEnum.Ignore;
        lbl.CustomMinimumSize = new Vector2(0, 52);
        lbl.AddThemeFontSizeOverride("font_size", 36);
        lbl.AddThemeColorOverride("font_color", SectionHeaderColor);
        lbl.AddThemeColorOverride("font_outline_color", new Color(0.15f, 0.10f, 0f));
        lbl.AddThemeConstantOverride("outline_size", 6);
        lbl.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.6f));
        lbl.AddThemeConstantOverride("shadow_offset_x", 2);
        lbl.AddThemeConstantOverride("shadow_offset_y", 3);
        lbl.SetTextAutoSize(text);
        return lbl;
    }

    private static Label CreateEncounterHeader(string text, string roomType)
    {
        var lbl = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Left,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        lbl.AddThemeFontSizeOverride("font_size", 26);

        Color color = roomType switch
        {
            "Boss" => new Color(0.95f, 0.35f, 0.35f),
            "Elite" => new Color(0.95f, 0.70f, 0.25f),
            _ => EncounterHeaderColor,
        };
        lbl.AddThemeColorOverride("font_color", color);
        lbl.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
        lbl.AddThemeConstantOverride("outline_size", 3);
        lbl.CustomMinimumSize = new Vector2(0, 38);
        return lbl;
    }

    private static Label CreateMiniSectionLabel(string text)
    {
        var lbl = new Label
        {
            Text = text,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        lbl.AddThemeFontSizeOverride("font_size", 18);
        lbl.AddThemeColorOverride("font_color", DimTextColor);
        lbl.CustomMinimumSize = new Vector2(0, 24);
        return lbl;
    }

    private static Control CreateStatChip(string label, long value, Color color)
    {
        var hbox = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        hbox.AddThemeConstantOverride("separation", 6);

        var labelNode = new Label
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        labelNode.AddThemeFontSizeOverride("font_size", 18);
        labelNode.AddThemeColorOverride("font_color", DimTextColor);
        hbox.AddChild(labelNode);

        var valueNode = new Label
        {
            Text = value.ToString("N0"),
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        valueNode.AddThemeFontSizeOverride("font_size", 22);
        valueNode.AddThemeColorOverride("font_color", color);
        hbox.AddChild(valueNode);

        return hbox;
    }

    private static Control CreateStatRow(string label, string value, Color valueColor)
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 16);
        row.CustomMinimumSize = new Vector2(0, 36);

        var lblNode = new Label
        {
            Text = label,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        lblNode.AddThemeFontSizeOverride("font_size", 22);
        lblNode.AddThemeColorOverride("font_color", StatLabelColor);
        row.AddChild(lblNode);

        var valNode = new Label
        {
            Text = value,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        valNode.AddThemeFontSizeOverride("font_size", 22);
        valNode.AddThemeColorOverride("font_color", valueColor);
        row.AddChild(valNode);

        return row;
    }

    private static Label CreateStatLabel(string text, Color color, int fontSize)
    {
        var lbl = new Label
        {
            Text = text,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        lbl.AddThemeFontSizeOverride("font_size", fontSize);
        lbl.AddThemeColorOverride("font_color", color);
        return lbl;
    }

    private static Control CreateSpacer(int height, bool horizontal = false)
    {
        return new Control
        {
            CustomMinimumSize = horizontal ? new Vector2(height, 0) : new Vector2(0, height),
            MouseFilter = MouseFilterEnum.Ignore,
        };
    }

    private static long SumPlayerStat(List<DmSidecarPlayer> players, Func<DmSidecarPlayer, long> selector)
    {
        long sum = 0;
        foreach (var p in players) sum += selector(p);
        return sum;
    }

    private static string ResolveEncounterName(string? encounterId)
    {
        if (string.IsNullOrEmpty(encounterId)) return "Unknown Encounter";
        try
        {
            string title = new LocString("encounters", encounterId + ".title").GetFormattedText();
            if (!string.IsNullOrEmpty(title) && title != encounterId + ".title")
                return title;
        }
        catch { }
        return encounterId;
    }
}
