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
using dubiousQOL.Utilities;
using ModTheme = dubiousQOL.UI.Theme;

namespace dubiousQOL.Patches;

internal static class RunHistoryStatsViewerModal
{
    public static void Open(DmSidecar sidecar)
    {
        var modal = ModalHelper.GetModal();
        if (modal == null)
        {
            MainFile.Logger.Warn("RunHistoryStats: NModalContainer not found");
            return;
        }

        if (sidecar.Scopes == null && (sidecar.Combats == null || sidecar.Combats.Count == 0))
        {
            modal.Add(ModalHelper.CreateErrorPanel("DubiousStatsError", "No detailed stats available for this run."));
            return;
        }

        try { modal.Add(new RunHistoryStatsViewer(sidecar), showBackstop: false); }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"RunHistoryStats open: {e.Message}\n{e.StackTrace}");
            modal.Add(ModalHelper.CreateErrorPanel("DubiousStatsError", "Failed to open stats viewer (see logs)."));
        }
    }
}

internal partial class RunHistoryStatsViewer : Control, IScreenContext
{
    private const int MaxSourceRows = 10;

    private static Font? _kreonFont;

    public Control? DefaultFocusedControl => null;

    private readonly DmSidecar _sidecar;
    private readonly List<int> _actIndices;
    private ScrollContainer _scroll = null!;
    private readonly List<Control> _pages = new();
    private readonly List<Node> _tabs = new();

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
        if (ModalHelper.CreateBackButton(this, "DubiousStatsBackButton") == null)
            AddChild(ModalHelper.CreateFallbackCloseButton());
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        ModalHelper.TryHandleEscape(inputEvent, this);
    }

    private void Build()
    {
        _kreonFont ??= FontHelper.Load("kreon-bold");

        // Build tab names.
        var tabNames = new List<string> { "Run Summary" };
        foreach (var actIdx in _actIndices)
            tabNames.Add($"Act {actIdx + 1}");

        // Create tab bar using TabHelper.
        var (tabBar, tabs) = TabHelper.CreateTabBar(tabNames.ToArray());
        tabBar.AnchorLeft = 0.5f; tabBar.AnchorRight = 0.5f;
        tabBar.AnchorTop = 0f; tabBar.AnchorBottom = 0f;
        tabBar.OffsetLeft = -400; tabBar.OffsetRight = 400;
        tabBar.OffsetTop = 80; tabBar.OffsetBottom = 150;
        AddChild(tabBar);
        _tabs.AddRange(tabs);

        // Fade mask — gradient TextureRect with clip_children clips content at edges.
        var fadeMask = StyleHelper.CreateScrollFadeMask(fadePixels: 100f, totalHeight: 890f);
        fadeMask.SetAnchorsPreset(LayoutPreset.FullRect);
        fadeMask.OffsetTop = 170;
        fadeMask.OffsetBottom = -20;
        AddChild(fadeMask);

        // Scrollable content area inside the fade mask.
        _scroll = new ScrollContainer
        {
            Name = "StatsScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _scroll.AnchorLeft = 0f; _scroll.AnchorRight = 1f;
        _scroll.AnchorTop = 0f; _scroll.AnchorBottom = 1f;
        _scroll.OffsetLeft = 320; _scroll.OffsetRight = -440;
        _scroll.OffsetTop = 0; _scroll.OffsetBottom = 0;
        fadeMask.AddChild(_scroll);

        // Replace the default Godot scrollbar with the game's styled scrollbar.
        StyleScrollbar(_scroll);

        // Build pages.
        _pages.Add(BuildRunSummaryPage());
        foreach (var actIdx in _actIndices)
            _pages.Add(BuildActPage(actIdx));

        // Show the first page and wire tab switching.
        if (_pages.Count > 0)
            _scroll.AddChild(_pages[0]);
        TabHelper.WireTabSwitching(_tabs, _pages, _scroll);
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

        // --- Per-Act Summaries (at the top, in a panel) ---
        if (_actIndices.Count > 0 && _sidecar.Combats != null)
        {
            vbox.AddChild(CreateSectionHeader("Run Totals"));

            var totalsPanel = CreateDarkPanel();
            var totalsVbox = new VBoxContainer();
            totalsVbox.AddThemeConstantOverride("separation", 16);
            totalsPanel.AddChild(totalsVbox);

            foreach (var actIdx in _actIndices)
            {
                var actCombats = _sidecar.Combats.Where(c => c.Act == actIdx).ToList();
                if (actCombats.Count == 0) continue;

                long actDmg = 0, actBlk = 0, actHp = 0;
                int actTurns = 0;
                foreach (var c in actCombats)
                {
                    actDmg += SumPlayerStat(c.Players, p => p.DamageDealt);
                    actBlk += SumPlayerStat(c.Players, p => p.BlockGained);
                    actHp += SumPlayerStat(c.Players, p => p.HpLost);
                    actTurns += c.PlayerTurns;
                }

                var actLabel = StyleHelper.CreateSubSectionHeader($"Act {actIdx + 1}", ModTheme.SectionHeader, 22, 0, 28f);
                totalsVbox.AddChild(actLabel);

                var actSummary = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
                actSummary.AddThemeConstantOverride("separation", 32);
                actSummary.AddChild(CreateStatChip("DMG", actDmg, ModTheme.Damage));
                actSummary.AddChild(CreateStatChip("BLK", actBlk, ModTheme.Block));
                actSummary.AddChild(CreateStatChip("HP\u2009Lost", actHp, ModTheme.HpLost));
                actSummary.AddChild(CreateStatChip("Turns", actTurns, ModTheme.TextLabel));
                actSummary.AddChild(CreateStatChip("Encounters", actCombats.Count, ModTheme.TextDim));
                totalsVbox.AddChild(actSummary);

                if (actIdx != _actIndices[^1])
                    totalsVbox.AddChild(StyleHelper.CreateDivider(ModTheme.Divider, 1f));
            }

            vbox.AddChild(totalsPanel);
        }

        // --- Per-Player Breakdowns ---
        if (runScope != null && runScope.Players.Count > 0)
        {
            foreach (var player in runScope.Players)
            {
                string playerLabel = FormatPlayerHeader(player, runScope.Players.Count > 1);
                vbox.AddChild(CreateSectionHeader(playerLabel));

                // Summary chips row.
                var summaryRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
                summaryRow.AddThemeConstantOverride("separation", 32);
                summaryRow.AddChild(CreateStatChip("DMG", player.DamageDealt, ModTheme.Damage));
                summaryRow.AddChild(CreateStatChip("BLK", player.BlockGained, ModTheme.Block));
                summaryRow.AddChild(CreateStatChip("HP\u2009Lost", player.HpLost, ModTheme.HpLost));
                summaryRow.AddChild(CreateStatChip("Turns", runScope.PlayerTurns, ModTheme.TextLabel));
                vbox.AddChild(summaryRow);

                // Damage breakdown.
                if (player.DamageBySource != null && player.DamageBySource.Count > 0)
                {
                    vbox.AddChild(CreateMiniSectionLabel($"Damage Sources \u2014 {player.DamageDealt:N0} total"));
                    vbox.AddChild(BuildBreakdownPanel(player.DamageBySource, player.DamageDealt, ModTheme.Damage));
                }

                // Block breakdown.
                if (player.BlockBySource != null && player.BlockBySource.Count > 0)
                {
                    vbox.AddChild(CreateMiniSectionLabel($"Block Sources \u2014 {player.BlockGained:N0} total"));
                    vbox.AddChild(BuildBreakdownPanel(player.BlockBySource, player.BlockGained, ModTheme.Block));
                }

                // HP lost breakdown.
                if (player.HpLostBySource != null && player.HpLostBySource.Count > 0)
                {
                    vbox.AddChild(CreateMiniSectionLabel($"HP Lost Sources \u2014 {player.HpLost:N0} total"));
                    vbox.AddChild(BuildBreakdownPanel(player.HpLostBySource, player.HpLost, ModTheme.HpLost));
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
                grid.AddChild(CreateStatRow("Damage Dealt", player.DamageDealt.ToString("N0"), ModTheme.Damage));
                grid.AddChild(CreateStatRow("Block Gained", player.BlockGained.ToString("N0"), ModTheme.Block));
                grid.AddChild(CreateStatRow("HP Lost", player.HpLost.ToString("N0"), ModTheme.HpLost));
                vbox.AddChild(panel);
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
            var empty = CreateStatLabel("No encounters recorded for this act.", ModTheme.TextDim, 22);
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
            summaryRow.AddChild(CreateStatChip("DMG", SumPlayerStat(combat.Players, p => p.DamageDealt), ModTheme.Damage));
            summaryRow.AddChild(CreateStatChip("BLK", SumPlayerStat(combat.Players, p => p.BlockGained), ModTheme.Block));
            summaryRow.AddChild(CreateStatChip("HP\u2009Lost", SumPlayerStat(combat.Players, p => p.HpLost), ModTheme.HpLost));
            summaryRow.AddChild(CreateStatChip("Turns", combat.PlayerTurns, ModTheme.TextLabel));
            content.AddChild(summaryRow);

            // Source breakdowns for the first player (single-player).
            // For multiplayer, iterate all players.
            foreach (var player in combat.Players)
            {
                if (combat.Players.Count > 1)
                {
                    var playerLabel = CreateStatLabel(
                        player.Character ?? $"Player {player.NetId}",
                        ModTheme.SubSectionHeader, 20);
                    playerLabel.CustomMinimumSize = new Vector2(0, 28);
                    content.AddChild(playerLabel);
                }

                if (player.DamageBySource != null && player.DamageBySource.Count > 0)
                {
                    content.AddChild(CreateMiniSectionLabel("Damage Sources"));
                    content.AddChild(BuildBreakdownPanel(player.DamageBySource, player.DamageDealt, ModTheme.Damage, compact: true));
                }
                if (player.BlockBySource != null && player.BlockBySource.Count > 0)
                {
                    content.AddChild(CreateMiniSectionLabel("Block Sources"));
                    content.AddChild(BuildBreakdownPanel(player.BlockBySource, player.BlockGained, ModTheme.Block, compact: true));
                }
                if (player.HpLostBySource != null && player.HpLostBySource.Count > 0)
                {
                    content.AddChild(CreateMiniSectionLabel("HP Lost Sources"));
                    content.AddChild(BuildBreakdownPanel(player.HpLostBySource, player.HpLost, ModTheme.HpLost, compact: true));
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
            vbox.AddChild(CreateSourceRow($"... {sorted.Count - MaxSourceRows} more", remaining, pct, ModTheme.TextDim, compact));
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
        nameLabel.AddThemeColorOverride("font_color", ModTheme.TextLabel);
        ApplyFont(nameLabel);
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
        valueLabel.AddThemeColorOverride("font_color", ModTheme.TextValue);
        ApplyFont(valueLabel);
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
        pctLabel.AddThemeColorOverride("font_color", ModTheme.TextDim);
        ApplyFont(pctLabel);
        row.AddChild(pctLabel);

        return root;
    }

    private static PanelContainer CreateDarkPanel() =>
        StyleHelper.CreateDarkPanel(ModTheme.PanelBgGame, cornerRadius: 0);

    private static Control CreateSectionHeader(string text) =>
        StyleHelper.CreateSectionHeader(text, ModTheme.SectionHeader);

    private static Control CreateEncounterHeader(string text, string roomType)
    {
        Color color = roomType switch
        {
            "Boss" => ModTheme.RoomBoss,
            "Elite" => ModTheme.RoomElite,
            _ => ModTheme.SubSectionHeader,
        };
        return StyleHelper.CreateSectionHeader(text, color, fontSize: 28, outlineSize: 5);
    }

    private static Label CreateMiniSectionLabel(string text) =>
        StyleHelper.CreateDimLabel(text);

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
        labelNode.AddThemeColorOverride("font_color", ModTheme.TextDim);
        ApplyFont(labelNode);
        hbox.AddChild(labelNode);

        var valueNode = new Label
        {
            Text = value.ToString("N0"),
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        valueNode.AddThemeFontSizeOverride("font_size", 22);
        valueNode.AddThemeColorOverride("font_color", color);
        ApplyFont(valueNode);
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
        lblNode.AddThemeColorOverride("font_color", ModTheme.TextLabel);
        ApplyFont(lblNode);
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
        ApplyFont(valNode);
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
        ApplyFont(lbl);
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

    private static string FormatPlayerHeader(DmSidecarPlayer player, bool isMultiplayer)
    {
        string character = player.Character ?? $"Player {player.NetId}";
        if (isMultiplayer && !string.IsNullOrEmpty(player.Name))
            return $"{player.Name} \u2014 {character}";
        return character;
    }

    private static void ApplyFont(Label label)
    {
        if (_kreonFont != null)
            label.AddThemeFontOverride("font", _kreonFont);
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

    /// <summary>
    /// Hides the default Godot VScrollBar and overlays the game's styled NScrollbar,
    /// syncing scroll position bidirectionally. The scrollbar is added as a sibling
    /// of the fadeMask so it's not affected by the fade clipping.
    /// </summary>
    private void StyleScrollbar(ScrollContainer scroll)
    {
        // The scrollbar IS the root of its scene, so instantiate directly
        // (ExtractFromScene would QueueFree the root, destroying what we just created).
        NScrollbar? gameScrollbar = null;
        try
        {
            var packed = ResourceLoader.Load<PackedScene>(
                "res://scenes/ui/scrollbar.tscn", null, ResourceLoader.CacheMode.Reuse);
            if (packed != null)
                gameScrollbar = packed.Instantiate<NScrollbar>(PackedScene.GenEditState.Disabled);
        }
        catch (Exception e) { MainFile.Logger.Warn($"RunHistoryStats scrollbar: {e.Message}"); }
        if (gameScrollbar == null)
        {
            MainFile.Logger.Warn("RunHistoryStats: could not instantiate game scrollbar");
            return;
        }

        // Hide the built-in VScrollBar visually (still drives scroll logic).
        var vbar = scroll.GetVScrollBar();
        vbar.AddThemeStyleboxOverride("scroll", new StyleBoxEmpty());
        vbar.AddThemeStyleboxOverride("grabber", new StyleBoxEmpty());
        vbar.AddThemeStyleboxOverride("grabber_highlight", new StyleBoxEmpty());
        vbar.AddThemeStyleboxOverride("grabber_pressed", new StyleBoxEmpty());
        vbar.CustomMinimumSize = new Vector2(0, 0);

        // Position the game scrollbar to the right of the content area.
        gameScrollbar.Name = "GameScrollbar";
        gameScrollbar.AnchorLeft = 0.5f; gameScrollbar.AnchorRight = 0.5f;
        gameScrollbar.AnchorTop = 0.5f; gameScrollbar.AnchorBottom = 0.5f;
        gameScrollbar.OffsetLeft = 600; gameScrollbar.OffsetRight = 648;
        gameScrollbar.OffsetTop = -323; gameScrollbar.OffsetBottom = 477;
        gameScrollbar.CustomMinimumSize = new Vector2(48, 800);
        AddChild(gameScrollbar);

        // Sync: ScrollContainer VScrollBar → game scrollbar.
        // The VScrollBar range is [0, maxValue] where maxValue = content height - visible height.
        // NScrollbar expects 0–100 percentage.
        vbar.ValueChanged += value =>
        {
            double range = vbar.MaxValue - vbar.Page;
            if (range > 0)
            {
                double pct = value / range * 100.0;
                gameScrollbar.SetValueWithoutAnimation(pct);
            }
        };

        // Sync: game scrollbar drag → ScrollContainer.
        gameScrollbar.Connect("value_changed", Callable.From<double>(val =>
        {
            double range = vbar.MaxValue - vbar.Page;
            if (range > 0 && gameScrollbar.MaxValue > 0)
            {
                double pct = val / gameScrollbar.MaxValue;
                scroll.ScrollVertical = (int)(pct * range);
            }
        }));

        // Show/hide based on whether content overflows.
        vbar.Connect("changed", Callable.From(() =>
        {
            gameScrollbar.Visible = vbar.MaxValue > vbar.Page;
        }));
    }
}
