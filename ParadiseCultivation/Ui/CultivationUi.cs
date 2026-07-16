using System.Numerics;
using ImGuiNET;

namespace ParadiseCultivation;

/// <summary>All ImGui panels of the cultivation slice — registered as one draw delegate on
/// the shared <c>ImGuiUiCore</c>, whose sim-thread half the <see cref="CultivationRunner"/>
/// pumps every fixed tick. Panels therefore run ON THE SIM THREAD: they read the runner's
/// <see cref="CultivationRunner.UiWorld"/> and side stores directly (BankHeist-style direct
/// ECS access) and mutate ONLY by enqueueing commands. The identical UI runs in the
/// standalone .NET runtime and the Godot play-mode host.
///
/// The world map follows the locked presentation direction: square logical grid drawn as a
/// 2:1 isometric diamond field (ink-wash placeholder palette, light grid lines), continuous
/// wheel zoom, drag to pan, the player as a small MOVING marker walking its travel path as
/// game days tick, observable-range fog, hover path preview, and WASD single-tile steps.</summary>
public sealed class CultivationUi
{
    private readonly CultivationRunner _runner;

    private int _seedInput;
    private int _presetIndex;
    private float _zoom;
    private Entity _selectedNpc;
    private bool _hasSelection;
    private string _chatInput = string.Empty;
    private string _mapHint = string.Empty;

    // Hover path preview cache — replan only when the hovered tile or the player moves.
    private (int X, int Y) _hoverTile = (-1, -1);
    private (int X, int Y) _hoverFrom = (-1, -1);
    private int _hoverRealm = -1;
    private TravelPlan? _hoverPlan;

    private Vector2 _dragOrigin;
    private bool _dragging;
    private WorldMap? _centeredMap; // scroll-to-player once per generated world

    private CultivationConfig Config => _runner.Config;
    private UiTextConfig T => Config.Text.Ui;
    private static string F(string template, params object[] args) => CultivationRules.F(template, args);

    public CultivationUi(CultivationRunner runner)
    {
        _runner = runner;
        _seedInput = runner.Map.Seed;
        _presetIndex = runner.Map.PresetIndex;
        _zoom = runner.Config.Ui.ZoomDefault;
    }

    public void Draw()
    {
        switch (_runner.Phase)
        {
            case GamePhase.NewGame:
                DrawNewGame();
                break;
            case GamePhase.Playing:
                DrawMap();
                DrawStatus();
                DrawActions();
                DrawLocation();
                DrawChronicle();
                break;
            case GamePhase.Dead:
                DrawMap();
                DrawChronicle();
                DrawDeath();
                break;
        }
    }

    private string SaveSlotPath => Path.Combine(_runner.SaveRoot, "slot1.json");

    // ---- new game ---------------------------------------------------------------------------

    private void DrawNewGame()
    {
        ImGui.SetNextWindowPos(new Vector2(40, 40), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(430, 0), ImGuiCond.FirstUseEver);
        // Auto-resize: the "Load Saved Journey" button appears only when a save exists and
        // must not be clipped by a height measured before it was there.
        ImGui.Begin(T.NewGameTitle, ImGuiWindowFlags.AlwaysAutoResize);

        ImGui.TextWrapped(T.NewGamePitch);
        ImGui.Separator();

        ImGui.InputInt(T.SeedLabel, ref _seedInput);
        var presets = Config.World.Presets;
        if (ImGui.BeginCombo(T.WorldLabel, presets[_presetIndex].Name))
        {
            for (var i = 0; i < presets.Length; i++)
            {
                if (ImGui.Selectable(presets[i].Name, i == _presetIndex)) _presetIndex = i;
            }
            ImGui.EndCombo();
        }

        if (ImGui.Button(T.RerollWorld))
        {
            _runner.RequestStartNew(_seedInput, _presetIndex);
            _hasSelection = false;
        }
        ImGui.SameLine();
        if (ImGui.Button(T.RerollFate))
        {
            _seedInput = unchecked(_seedInput * 7919 + 13);
            _runner.RequestStartNew(_seedInput, _presetIndex);
            _hasSelection = false;
        }

        ImGui.Separator();
        var world = _runner.UiWorld;
        ref readonly var player = ref world.GetComponent<PlayerData>(_runner.Player);
        ImGui.Text(F(T.NameLine, CultivationRules.PlayerName(Config, in player)));
        ImGui.Text(F(T.SpiritRootLine, Config.SpiritRoots.Elements[player.SpiritRootElement],
            Config.SpiritRoots.Grades[player.SpiritRootGrade].Name,
            $"{Config.SpiritRoots.Grades[player.SpiritRootGrade].Multiplier:F1}"));
        ImGui.Text(F(T.CharmLine, Config.CharmTiers[player.CharmTier].Name));
        ImGui.Text(F(T.WorldSummaryLine, _runner.Map.Sites.Count(s => s.Kind == SiteKind.Town),
            _runner.Map.Sites.Count(s => s.Kind == SiteKind.Sect)));

        ImGui.Separator();
        if (ImGui.Button(T.BeginJourney, new Vector2(-1, 0)))
        {
            _runner.RequestBeginJourney();
        }
        if (File.Exists(SaveSlotPath) && ImGui.Button(T.LoadSavedJourney, new Vector2(-1, 0)))
        {
            _runner.RequestLoad(SaveSlotPath);
            _hasSelection = false;
        }
        ImGui.End();

        DrawMap();
    }

    // ---- isometric world map ----------------------------------------------------------------

    private void DrawMap()
    {
        var map = _runner.Map;
        var ui = Config.Ui;
        var world = _runner.UiWorld;
        ref readonly var player = ref world.GetComponent<PlayerData>(_runner.Player);
        var realmIndex = world.GetComponent<Cultivator>(_runner.Player).RealmIndex;
        var playing = _runner.Phase == GamePhase.Playing;

        ImGui.SetNextWindowPos(new Vector2(480, 40), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(640, 560), ImGuiCond.FirstUseEver);
        ImGui.Begin(T.MapTitle);
        ImGui.TextDisabled(F(T.MapHelp, map.Width, map.Height));
        ImGui.BeginChild("map_scroll", new Vector2(0, _mapHint.Length > 0 ? -ImGui.GetTextLineHeightWithSpacing() : 0),
            ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        var halfW = _zoom * 0.5f;
        var halfH = _zoom * 0.25f;
        var canvas = new Vector2((map.Width + map.Height) * halfW, (map.Width + map.Height) * halfH + halfH * 2f);

        // Center the view on the player when a new world appears, and keep following while
        // a journey animates (the marker stays in sight as the days tick by).
        var follow = !ReferenceEquals(_centeredMap, map) || _runner.ActiveTravel.Plan is not null;
        if (follow)
        {
            _centeredMap = map;
            var local = new Vector2(
                (player.X - player.Y + map.Height) * halfW,
                (player.X + player.Y + 1f) * halfH);
            var view = ImGui.GetWindowSize();
            ImGui.SetScrollX(local.X - view.X * 0.5f);
            ImGui.SetScrollY(local.Y - view.Y * 0.5f);
        }

        var origin = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton("map", canvas);
        var hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();

        // Continuous zoom on the wheel, anchored at the cursor (scroll adjusts to match).
        if (hovered && io.MouseWheel != 0f)
        {
            var mouseLocal = ImGui.GetMousePos() - origin;
            var oldZoom = _zoom;
            _zoom = Math.Clamp(_zoom * (1f + io.MouseWheel * 0.12f), ui.ZoomMin, ui.ZoomMax);
            var scale = _zoom / oldZoom;
            ImGui.SetScrollX(ImGui.GetScrollX() + mouseLocal.X * (scale - 1f));
            ImGui.SetScrollY(ImGui.GetScrollY() + mouseLocal.Y * (scale - 1f));
        }

        // Left-drag pans; a short press-release without drag is a travel click.
        var clicked = false;
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 6f))
        {
            if (!_dragging)
            {
                _dragging = true;
                _dragOrigin = ImGui.GetMousePos();
            }
            var delta = ImGui.GetMousePos() - _dragOrigin;
            _dragOrigin = ImGui.GetMousePos();
            ImGui.SetScrollX(ImGui.GetScrollX() - delta.X);
            ImGui.SetScrollY(ImGui.GetScrollY() - delta.Y);
        }
        else if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            clicked = hovered && !_dragging;
            _dragging = false;
        }

        var drawList = ImGui.GetWindowDrawList();
        var clipMin = ImGui.GetWindowPos();
        var clipMax = clipMin + ImGui.GetWindowSize();
        var margin = _zoom;

        Vector2 Center(float x, float y) =>
            origin + new Vector2((x - y + map.Height) * halfW, (x + y + 1f) * halfH);

        // Interpolated player position: the marker WALKS its path as game days tick.
        var playerPos = new Vector2(player.X, player.Y);
        var (travelPlan, daysDone) = _runner.ActiveTravel;
        var remainingFrom = 0;
        if (travelPlan is not null)
        {
            var idx = 0;
            while (idx < travelPlan.Steps.Count && travelPlan.CumulativeDays[idx] <= daysDone) idx++;
            remainingFrom = idx;
            if (idx < travelPlan.Steps.Count)
            {
                var prevCum = idx == 0 ? 0.0 : travelPlan.CumulativeDays[idx - 1];
                var span = travelPlan.CumulativeDays[idx] - prevCum;
                var t = span <= 0 ? 0f : (float)Math.Clamp((daysDone - prevCum) / span, 0.0, 1.0);
                var next = travelPlan.Steps[idx];
                playerPos = Vector2.Lerp(new Vector2(player.X, player.Y), new Vector2(next.X, next.Y), t);
            }
        }

        var range = ui.ObservableRange;
        var showGrid = _zoom >= 10f;
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                var c = Center(x, y);
                if (c.X < clipMin.X - margin || c.X > clipMax.X + margin ||
                    c.Y < clipMin.Y - margin || c.Y > clipMax.Y + margin)
                {
                    continue;
                }

                ref readonly var tile = ref map.TileAt(x, y);
                var top = c with { Y = c.Y - halfH };
                var right = c with { X = c.X + halfW };
                var bottom = c with { Y = c.Y + halfH };
                var left = c with { X = c.X - halfW };
                drawList.AddQuadFilled(top, right, bottom, left, ui.TerrainColors[(int)tile.Terrain]);
                if (showGrid)
                {
                    drawList.AddQuad(top, right, bottom, left, ui.GridLineColor);
                }

                var inRange = Math.Max(Math.Abs(x - player.X), Math.Abs(y - player.Y)) <= range;
                if (tile.VeinQuality > 0 && inRange)
                {
                    drawList.AddCircleFilled(c, MathF.Max(1.5f, halfH * 0.4f), ui.VeinQualityColors[tile.VeinQuality]);
                }
                if (playing && !inRange)
                {
                    drawList.AddQuadFilled(top, right, bottom, left, ui.FogColor);
                }
            }
        }

        foreach (var site in map.Sites)
        {
            var c = Center(site.X, site.Y);
            if (c.X < clipMin.X - margin || c.X > clipMax.X + margin ||
                c.Y < clipMin.Y - margin || c.Y > clipMax.Y + margin)
            {
                continue;
            }
            var radius = MathF.Max(3f, halfH * 0.9f);
            drawList.AddCircleFilled(c, radius, site.Kind == SiteKind.Town ? ui.TownColor : ui.SectColor);
            drawList.AddCircle(c, radius, 0xFF000000);
            if (_zoom >= ui.LabelZoomThreshold)
            {
                drawList.AddText(c + new Vector2(radius + 2f, -radius), 0xFFEFEFEF, site.Name);
            }
        }

        // Remaining travel path.
        if (travelPlan is not null)
        {
            var prev = Center(playerPos.X, playerPos.Y);
            for (var i = remainingFrom; i < travelPlan.Steps.Count; i++)
            {
                var next = Center(travelPlan.Steps[i].X, travelPlan.Steps[i].Y);
                drawList.AddLine(prev, next, ui.PathColor, 2f);
                prev = next;
            }
        }

        // The player: a small traveling figure (head + robe) at the interpolated position.
        var pc = Center(playerPos.X, playerPos.Y);
        var s = MathF.Max(3f, halfH);
        drawList.AddTriangleFilled(
            pc + new Vector2(0, -s * 0.6f), pc + new Vector2(s * 0.7f, s * 0.9f), pc + new Vector2(-s * 0.7f, s * 0.9f),
            ui.PlayerColor);
        drawList.AddCircleFilled(pc + new Vector2(0, -s * 0.9f), s * 0.45f, ui.PlayerColor);

        // Hover: tooltip + cached path preview; click travels (within observable range only).
        if (hovered && !_dragging)
        {
            var m = ImGui.GetMousePos() - origin;
            var a = m.X / halfW - map.Height; // x - y
            var b = m.Y / halfH - 1f;         // x + y
            var tx = (int)MathF.Round((a + b) * 0.5f);
            var ty = (int)MathF.Round((b - a) * 0.5f);
            if (map.InBounds(tx, ty))
            {
                ref readonly var tile = ref map.TileAt(tx, ty);
                var inRange = Math.Max(Math.Abs(tx - player.X), Math.Abs(ty - player.Y)) <= range;

                ImGui.BeginTooltip();
                ImGui.Text(T.TerrainNames[(int)tile.Terrain]);
                if (tile.VeinQuality > 0 && inRange) ImGui.Text(F(T.VeinTooltip, tile.VeinQuality));
                if (tile.SiteIndex >= 0 && inRange)
                {
                    var site = map.Sites[tile.SiteIndex];
                    ImGui.Text($"{(site.Kind == SiteKind.Town ? T.TownWord : T.SectWord)}: {site.Name}");
                }

                if (!inRange)
                {
                    ImGui.TextDisabled(T.BeyondSight);
                }
                else if (playing && !_runner.Busy && (tx != player.X || ty != player.Y))
                {
                    if (_hoverTile != (tx, ty) || _hoverFrom != (player.X, player.Y) || _hoverRealm != realmIndex)
                    {
                        _hoverTile = (tx, ty);
                        _hoverFrom = (player.X, player.Y);
                        _hoverRealm = realmIndex;
                        _hoverPlan = CultivationRules.PlanTravel(Config, map, player.X, player.Y, realmIndex, tx, ty);
                    }
                    if (_hoverPlan is { } preview)
                    {
                        ImGui.Text(F(T.TravelTooltip, preview.TotalDays, preview.Mode));
                        var prev = Center(player.X, player.Y);
                        foreach (var (sx, sy) in preview.Steps)
                        {
                            var next = Center(sx, sy);
                            drawList.AddLine(prev, next, ui.PathColor, 1.5f);
                            prev = next;
                        }
                        if (clicked)
                        {
                            _runner.RequestTravel(tx, ty);
                            _hasSelection = false;
                            _mapHint = string.Empty;
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled(T.NoFootPath);
                    }
                }
                ImGui.EndTooltip();

                if (clicked && !inRange)
                {
                    _mapHint = T.TooFarHint;
                }
            }
        }

        ImGui.EndChild();
        if (_mapHint.Length > 0)
        {
            ImGui.TextDisabled(_mapHint);
        }
        ImGui.End();

        // WASD single-tile steps (four-direction adjacency) — outside text inputs only.
        if (playing && !_runner.Busy && !io.WantTextInput)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.W, true)) _runner.RequestTravelStep(0, -1);
            else if (ImGui.IsKeyPressed(ImGuiKey.S, true)) _runner.RequestTravelStep(0, 1);
            else if (ImGui.IsKeyPressed(ImGuiKey.A, true)) _runner.RequestTravelStep(-1, 0);
            else if (ImGui.IsKeyPressed(ImGuiKey.D, true)) _runner.RequestTravelStep(1, 0);
        }
    }

    // ---- status -------------------------------------------------------------------------------

    private void DrawStatus()
    {
        var world = _runner.UiWorld;
        ref readonly var cultivator = ref world.GetComponent<Cultivator>(_runner.Player);
        ref readonly var player = ref world.GetComponent<PlayerData>(_runner.Player);

        ImGui.SetNextWindowPos(new Vector2(40, 40), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(420, 320), ImGuiCond.FirstUseEver);
        ImGui.Begin(T.StatusTitle);

        ImGui.Text($"{CultivationRules.PlayerName(Config, in player)}: " +
            $"{CultivationRules.RealmTitle(Config, cultivator.RealmIndex, cultivator.SubStage)}");
        ImGui.Text(_runner.Today);
        var ageYears = cultivator.AgeDays / CultivationRules.DaysPerYear(Config);
        ImGui.Text(F(T.AgeLine, $"{ageYears:F1}", $"{player.LifespanYears:N0}"));

        var realm = Config.Realms[cultivator.RealmIndex];
        var progress = (float)Math.Clamp(cultivator.CultivationPoints / realm.PointsPerSubStage, 0.0, 1.0);
        var monthly = CultivationRules.MonthlyCultivationGain(Config, _runner.Map, in cultivator, in player);
        ImGui.ProgressBar(progress, new Vector2(-1, 0),
            F(T.ProgressLine, $"{cultivator.CultivationPoints:F0}", realm.PointsPerSubStage, $"{monthly:F0}"));

        ImGui.Text(F(T.SpiritRootLine, Config.SpiritRoots.Elements[player.SpiritRootElement],
            Config.SpiritRoots.Grades[player.SpiritRootGrade].Name,
            $"{Config.SpiritRoots.Grades[player.SpiritRootGrade].Multiplier:F1}"));
        ImGui.Text(F(T.CharmFortuneLine, Config.CharmTiers[player.CharmTier].Name, $"{player.Fortune:F0}",
            $"{CultivationRules.FortuneMultiplier(Config, player.Fortune):F2}"));
        ImGui.Text(F(T.StonesHerbsLine, player.SpiritStones, player.Herbs));
        if (player.Pills > 0)
        {
            ImGui.Text(F(T.PillsLine, player.Pills));
        }
        if (player.InjuryMonths > 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), F(T.InjuredLine, player.InjuryMonths));
        }
        var veinBonus = CultivationRules.VeinBonusAt(Config, _runner.Map, player.X, player.Y);
        if (veinBonus > 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.9f, 1f, 1f), F(T.OnVeinLine, $"{veinBonus:P0}"));
        }

        if (_runner.Busy)
        {
            var (remaining, total) = _runner.PendingProgress;
            var fraction = total <= 0 ? 0f : (float)(1.0 - remaining / total);
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), F(T.TimeFlowsLine, $"{remaining:F0}"));
        }
        ImGui.End();
    }

    // ---- actions ------------------------------------------------------------------------------

    private int _cultivateMonths = 3;
    private int _secludeYears = 5;

    private void DrawActions()
    {
        ImGui.SetNextWindowPos(new Vector2(40, 380), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(420, 300), ImGuiCond.FirstUseEver);
        ImGui.Begin(T.ActionsTitle);

        var world = _runner.UiWorld;
        ref readonly var cultivator = ref world.GetComponent<Cultivator>(_runner.Player);
        ref readonly var player = ref world.GetComponent<PlayerData>(_runner.Player);

        ImGui.BeginDisabled(_runner.Busy);

        ImGui.SliderInt($"{T.MonthsLabel}##cultivate", ref _cultivateMonths, 1, 12);
        ImGui.SameLine();
        if (ImGui.Button(T.CultivateButton)) _runner.RequestCultivate(_cultivateMonths);

        ImGui.SliderInt($"{T.YearsLabel}##seclude", ref _secludeYears, 1, 100);
        ImGui.SameLine();
        if (ImGui.Button(T.SecludeButton)) _runner.RequestSeclude(_secludeYears);

        if (ImGui.Button(T.ExploreButton)) _runner.RequestExplore();

        ImGui.Separator();
        if (CultivationRules.BreakthroughReady(Config, in cultivator))
        {
            var chance = CultivationRules.BreakthroughSuccessChance(Config, _runner.Map, in cultivator, in player);
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.4f, 1f), F(T.BreakthroughReadyLine, $"{chance:P0}"));
            if (player.Pills > 0)
            {
                ImGui.TextColored(new Vector4(0.6f, 1f, 0.6f, 1f),
                    F(T.PillReadyLine, $"{Config.Trade.PillBreakthroughBonus:P0}"));
            }
            if (ImGui.Button(T.BreakthroughButton, new Vector2(-1, 0)))
            {
                _runner.RequestBreakthrough();
            }
        }
        else
        {
            ImGui.TextDisabled(T.BreakthroughLockedLine);
        }

        ImGui.Separator();
        if (ImGui.Button(T.SaveButton)) _runner.RequestSave(SaveSlotPath);
        ImGui.SameLine();
        ImGui.BeginDisabled(!File.Exists(SaveSlotPath));
        if (ImGui.Button(T.LoadButton))
        {
            _runner.RequestLoad(SaveSlotPath);
            _hasSelection = false;
        }
        ImGui.EndDisabled();

        ImGui.EndDisabled();

        if (_runner.LastActionResult.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextWrapped(_runner.LastActionResult);
        }
        ImGui.End();
    }

    // ---- location & NPCs ------------------------------------------------------------------------

    private void DrawLocation()
    {
        var map = _runner.Map;
        var world = _runner.UiWorld;
        ref readonly var player = ref world.GetComponent<PlayerData>(_runner.Player);
        ref readonly var tile = ref map.TileAt(player.X, player.Y);

        ImGui.SetNextWindowPos(new Vector2(1140, 40), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(420, 560), ImGuiCond.FirstUseEver);
        ImGui.Begin(T.LocationTitle);

        if (tile.SiteIndex < 0)
        {
            ImGui.TextWrapped(tile.VeinQuality > 0
                ? F(T.WildernessVeinLine, tile.VeinQuality, T.TerrainNames[(int)tile.Terrain])
                : F(T.WildernessLine, T.TerrainNames[(int)tile.Terrain]));
            ImGui.End();
            return;
        }

        var site = map.Sites[tile.SiteIndex];
        ImGui.Text($"{(site.Kind == SiteKind.Town ? T.TownWord : T.SectWord)}: {site.Name}");
        ImGui.Separator();

        if (site.Kind == SiteKind.Town)
        {
            DrawMarket(tile.SiteIndex, in player);
        }

        var found = false;
        foreach (var entity in _runner.Npcs)
        {
            ref readonly var npc = ref world.GetComponent<NpcState>(entity);
            if (npc.Alive == 0 || npc.SiteIndex != tile.SiteIndex) continue;
            var realmIndex = world.GetComponent<Cultivator>(entity).RealmIndex;
            var label = $"{CultivationRules.NpcName(Config, in npc)}" +
                $"{(npc.IsLeader != 0 ? T.SectLeaderTag : string.Empty)}: {Config.Realms[realmIndex].Name}";
            var selected = _hasSelection && entity == _selectedNpc;
            if (ImGui.Selectable(label, selected))
            {
                _selectedNpc = entity;
                _hasSelection = true;
            }
            found |= _hasSelection && entity == _selectedNpc;
        }

        if (!found)
        {
            ImGui.TextDisabled(T.SelectSomeone);
            ImGui.End();
            return;
        }

        ImGui.Separator();
        DrawNpcPanel(_selectedNpc);
        ImGui.End();
    }

    /// <summary>The town market: herbs sell here, breakthrough pills stock here (monthly),
    /// and each town's deterministic price factor makes distant markets worth the trip.</summary>
    private void DrawMarket(int siteIndex, in PlayerData player)
    {
        ImGui.SeparatorText(T.MarketTitle);

        var herbPrice = CultivationRules.HerbSellStones(Config, _runner.Map, siteIndex);
        var pillPrice = CultivationRules.PillCostStones(Config, _runner.Map, siteIndex);
        var stock = _runner.TownPillStock[siteIndex];

        ImGui.BeginDisabled(_runner.Busy);

        ImGui.Text(F(T.MarketHerbLine, player.Herbs, herbPrice));
        ImGui.SameLine();
        ImGui.BeginDisabled(player.Herbs <= 0);
        if (ImGui.Button(T.SellOneButton)) _runner.RequestSellHerbs(1);
        ImGui.SameLine();
        if (ImGui.Button(T.SellAllButton)) _runner.RequestSellHerbs(player.Herbs);
        ImGui.EndDisabled();

        ImGui.Text(F(T.MarketPillLine, stock, pillPrice));
        ImGui.SameLine();
        ImGui.BeginDisabled(stock <= 0 || player.SpiritStones < pillPrice);
        if (ImGui.Button(T.BuyPillButton)) _runner.RequestBuyPill();
        ImGui.EndDisabled();

        ImGui.EndDisabled();
        ImGui.Separator();
    }

    private void DrawNpcPanel(Entity entity)
    {
        var world = _runner.UiWorld;
        ref readonly var npc = ref world.GetComponent<NpcState>(entity);
        ref readonly var npcCultivator = ref world.GetComponent<Cultivator>(entity);
        var personality = Config.Npc.Personalities[npc.PersonalityIndex];

        // Paper-doll placeholder: a portrait block tinted per personality (the design doc's
        // modular portrait system is future work). Deterministic tint via the personality index.
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var portrait = new Vector2(64, 80);
        var hue = 0xFF400060u + (uint)(npc.PersonalityIndex * 0x1810) % 0x8000;
        drawList.AddRectFilled(pos, pos + portrait, hue, 6f);
        drawList.AddRect(pos, pos + portrait, 0xFFB0B0B0, 6f);
        ImGui.Dummy(portrait with { X = portrait.X + 8 });
        ImGui.SameLine();

        ImGui.BeginGroup();
        ImGui.Text($"{CultivationRules.NpcName(Config, in npc)} ({personality})");
        ImGui.Text(CultivationRules.RealmTitle(Config, npcCultivator.RealmIndex, npcCultivator.SubStage));
        ImGui.Text(F(T.NpcAgeLine, $"{npcCultivator.AgeDays / CultivationRules.DaysPerYear(Config):F0}",
            $"{Config.Realms[npcCultivator.RealmIndex].LifespanYears:N0}"));
        ImGui.Text(F(T.CharmLine, Config.CharmTiers[npc.CharmTier].Name));
        ImGui.EndGroup();

        ImGui.Text(F(T.TheirRegardLine, $"{npc.AffectionToPlayer:F0}", CultivationRules.AffectionTierName(Config, npc.AffectionToPlayer)));
        ImGui.Text(F(T.YourRegardLine, $"{npc.PlayerAffection:F0}", CultivationRules.AffectionTierName(Config, npc.PlayerAffection)));

        ImGui.BeginDisabled(_runner.Busy);
        if (ImGui.Button(F(T.GiftButton, Config.Interaction.GiftSpiritStones))) _runner.RequestGift(entity);
        ImGui.SameLine();
        if (ImGui.Button(T.SparButton)) _runner.RequestSpar(entity);

        ImGui.SetNextItemWidth(-70);
        var send = ImGui.InputText("##chat", ref _chatInput, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        send |= ImGui.Button(T.SayButton);
        if (send && _chatInput.Length > 0)
        {
            _runner.RequestChat(entity, _chatInput);
            _chatInput = string.Empty;
        }
        ImGui.EndDisabled();

        if (_runner.LastReply.Length > 0)
        {
            ImGui.TextWrapped(_runner.LastReply);
        }

        var memories = _runner.MemoriesOf(entity);
        if (memories.Count > 0)
        {
            ImGui.SeparatorText(T.TheyRemember);
            var start = Math.Max(0, memories.Count - Config.Interaction.MemoryWindow);
            for (var i = memories.Count - 1; i >= start; i--)
            {
                ImGui.TextWrapped($"{CultivationRules.FormatDate(Config, memories[i].Day)}: {memories[i].Summary}");
            }
        }
    }

    // ---- chronicle & death ----------------------------------------------------------------------

    private void DrawChronicle()
    {
        ImGui.SetNextWindowPos(new Vector2(480, 620), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(640, 160), ImGuiCond.FirstUseEver);
        ImGui.Begin(T.ChronicleTitle);
        ImGui.BeginChild("chronicle_scroll");
        foreach (var entry in _runner.Chronicle)
        {
            ImGui.TextWrapped($"{CultivationRules.FormatDate(Config, entry.Day)}: {entry.Summary}");
        }
        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f) ImGui.SetScrollHereY(1f);
        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawDeath()
    {
        var world = _runner.UiWorld;
        ref readonly var cultivator = ref world.GetComponent<Cultivator>(_runner.Player);
        ref readonly var player = ref world.GetComponent<PlayerData>(_runner.Player);

        ImGui.SetNextWindowPos(new Vector2(40, 40), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(420, 220), ImGuiCond.FirstUseEver);
        ImGui.Begin(T.DeathTitle);
        var ageYears = cultivator.AgeDays / CultivationRules.DaysPerYear(Config);
        ImGui.TextWrapped(F(T.DeathLine, CultivationRules.PlayerName(Config, in player),
            CultivationRules.RealmTitle(Config, cultivator.RealmIndex, cultivator.SubStage), $"{ageYears:F0}"));
        ImGui.Separator();
        if (ImGui.Button(T.ReincarnateButton, new Vector2(-1, 0)))
        {
            _seedInput = unchecked(_seedInput * 48271 + 7);
            _runner.RequestStartNew(_seedInput, _presetIndex);
            _hasSelection = false;
            _chatInput = string.Empty;
            _mapHint = string.Empty;
        }
        ImGui.End();
    }
}
