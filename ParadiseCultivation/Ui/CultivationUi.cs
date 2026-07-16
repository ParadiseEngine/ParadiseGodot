using System.Numerics;
using ImGuiNET;

namespace ParadiseCultivation;

/// <summary>All ImGui panels of the cultivation slice — registered as one draw delegate on
/// the shared <c>ImGuiUiCore</c>, whose sim-thread half the <see cref="CultivationRunner"/>
/// pumps every fixed tick. Panels therefore run ON THE SIM THREAD: they read the runner's
/// <see cref="CultivationRunner.UiWorld"/> and side stores directly (BankHeist-style direct
/// ECS access) and mutate ONLY by enqueueing commands. The identical UI runs in the
/// standalone .NET runtime and the Godot play-mode host.</summary>
public sealed class CultivationUi
{
    private readonly CultivationRunner _runner;

    private int _seedInput;
    private int _sizeIndex;
    private int _tileSize;
    private int _cultivateMonths = 3;
    private int _secludeYears = 5;
    private Entity _selectedNpc;
    private bool _hasSelection;
    private string _chatInput = string.Empty;

    private CultivationConfig Config => _runner.Config;

    public CultivationUi(CultivationRunner runner)
    {
        _runner = runner;
        _seedInput = runner.Map.Seed;
        _sizeIndex = runner.Map.SizeIndex;
        _tileSize = runner.Config.Ui.TileSizeDefault;
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

    // ---- new game ---------------------------------------------------------------------------

    private void DrawNewGame()
    {
        ImGui.SetNextWindowPos(new Vector2(40, 40), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.FirstUseEver);
        ImGui.Begin("A New Cultivator");

        ImGui.TextWrapped("A living world of sects, spirit veins and finite lifespans. Reroll until the heavens deal you a world worth a legend.");
        ImGui.Separator();

        ImGui.InputInt("World seed", ref _seedInput);
        var sizes = Config.World.Sizes;
        if (ImGui.BeginCombo("World size", sizes[_sizeIndex].Name))
        {
            for (var i = 0; i < sizes.Length; i++)
            {
                if (ImGui.Selectable(sizes[i].Name, i == _sizeIndex)) _sizeIndex = i;
            }
            ImGui.EndCombo();
        }

        if (ImGui.Button("Reroll World"))
        {
            _runner.RequestStartNew(_seedInput, _sizeIndex);
            _hasSelection = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Reroll Fate"))
        {
            _seedInput = unchecked(_seedInput * 7919 + 13);
            _runner.RequestStartNew(_seedInput, _sizeIndex);
            _hasSelection = false;
        }

        ImGui.Separator();
        var world = _runner.UiWorld;
        ref readonly var player = ref world.GetComponent<PlayerData>(_runner.Player);
        ImGui.Text($"Name: {CultivationRules.PlayerName(Config, in player)}");
        ImGui.Text($"Spirit Root: {Config.SpiritRoots.Elements[player.SpiritRootElement]}: " +
            $"{Config.SpiritRoots.Grades[player.SpiritRootGrade].Name} (x{Config.SpiritRoots.Grades[player.SpiritRootGrade].Multiplier:F1})");
        ImGui.Text($"Charm: {Config.CharmTiers[player.CharmTier].Name}");
        ImGui.Text($"World: {_runner.Map.Sites.Count(s => s.Kind == SiteKind.Town)} towns, " +
            $"{_runner.Map.Sites.Count(s => s.Kind == SiteKind.Sect)} sects");

        ImGui.Separator();
        if (ImGui.Button("Begin the Journey", new Vector2(-1, 0)))
        {
            _runner.RequestBeginJourney();
        }
        ImGui.End();

        DrawMap();
    }

    // ---- map ----------------------------------------------------------------------------------

    private void DrawMap()
    {
        var map = _runner.Map;
        var ui = Config.Ui;
        var world = _runner.UiWorld;
        ref readonly var player = ref world.GetComponent<PlayerData>(_runner.Player);

        ImGui.SetNextWindowPos(new Vector2(480, 40), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(640, 560), ImGuiCond.FirstUseEver);
        ImGui.Begin("World Map");
        ImGui.SliderInt("Tile size", ref _tileSize, ui.TileSizeMin, ui.TileSizeMax);
        ImGui.BeginChild("map_scroll", new Vector2(0, 0), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(map.Width * _tileSize, map.Height * _tileSize);

        // One invisible button claims interaction for the whole grid.
        ImGui.InvisibleButton("map", size);
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);

        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                ref readonly var tile = ref map.TileAt(x, y);
                var color = tile.Terrain == Terrain.SpiritVein
                    ? ui.VeinQualityColors[tile.VeinQuality]
                    : ui.TerrainColors[(int)tile.Terrain];
                var min = origin + new Vector2(x * _tileSize, y * _tileSize);
                drawList.AddRectFilled(min, min + new Vector2(_tileSize, _tileSize), color);
            }
        }

        foreach (var site in map.Sites)
        {
            var center = origin + new Vector2((site.X + 0.5f) * _tileSize, (site.Y + 0.5f) * _tileSize);
            var radius = MathF.Max(3f, _tileSize * 0.6f);
            drawList.AddCircleFilled(center, radius, site.Kind == SiteKind.Town ? ui.TownColor : ui.SectColor);
            drawList.AddCircle(center, radius, 0xFF000000);
        }

        var marker = origin + new Vector2((player.X + 0.5f) * _tileSize, (player.Y + 0.5f) * _tileSize);
        drawList.AddTriangleFilled(
            marker + new Vector2(0, -_tileSize * 0.8f),
            marker + new Vector2(_tileSize * 0.6f, _tileSize * 0.5f),
            marker + new Vector2(-_tileSize * 0.6f, _tileSize * 0.5f),
            ui.PlayerColor);

        if (hovered)
        {
            var mouse = ImGui.GetMousePos() - origin;
            var tx = (int)(mouse.X / _tileSize);
            var ty = (int)(mouse.Y / _tileSize);
            if (map.InBounds(tx, ty))
            {
                ref readonly var tile = ref map.TileAt(tx, ty);
                ImGui.BeginTooltip();
                ImGui.Text(tile.Terrain == Terrain.SpiritVein
                    ? $"Spirit Vein (quality {tile.VeinQuality})"
                    : tile.Terrain.ToString());
                if (tile.SiteIndex >= 0)
                {
                    var site = map.Sites[tile.SiteIndex];
                    ImGui.Text($"{(site.Kind == SiteKind.Town ? "Town" : "Sect")}: {site.Name}");
                }
                var canTravel = _runner.Phase == GamePhase.Playing && !_runner.Busy &&
                                (tx != player.X || ty != player.Y);
                if (canTravel)
                {
                    var realmIndex = world.GetComponent<Cultivator>(_runner.Player).RealmIndex;
                    var (days, mode) = CultivationRules.PlanTravel(Config, map, player.X, player.Y, realmIndex, tx, ty);
                    ImGui.Text($"Travel: {days} day(s) {mode}: click to go");
                }
                ImGui.EndTooltip();

                if (canTravel && clicked)
                {
                    _runner.RequestTravel(tx, ty);
                    _hasSelection = false;
                }
            }
        }

        ImGui.EndChild();
        ImGui.End();
    }

    // ---- status -------------------------------------------------------------------------------

    private void DrawStatus()
    {
        var world = _runner.UiWorld;
        ref readonly var cultivator = ref world.GetComponent<Cultivator>(_runner.Player);
        ref readonly var player = ref world.GetComponent<PlayerData>(_runner.Player);

        ImGui.SetNextWindowPos(new Vector2(40, 40), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(420, 320), ImGuiCond.FirstUseEver);
        ImGui.Begin("Cultivator");

        ImGui.Text($"{CultivationRules.PlayerName(Config, in player)}: " +
            $"{CultivationRules.RealmTitle(Config, cultivator.RealmIndex, cultivator.SubStage)}");
        ImGui.Text(_runner.Today);
        var ageYears = cultivator.AgeDays / CultivationRules.DaysPerYear(Config);
        ImGui.Text($"Age {ageYears:F1} / lifespan {player.LifespanYears:N0} years");

        var realm = Config.Realms[cultivator.RealmIndex];
        var progress = (float)Math.Clamp(cultivator.CultivationPoints / realm.PointsPerSubStage, 0.0, 1.0);
        var monthly = CultivationRules.MonthlyCultivationGain(Config, _runner.Map, in cultivator, in player);
        ImGui.ProgressBar(progress, new Vector2(-1, 0),
            $"{cultivator.CultivationPoints:F0} / {realm.PointsPerSubStage} ({monthly:F0}/month here)");

        ImGui.Text($"Spirit Root: {Config.SpiritRoots.Elements[player.SpiritRootElement]}: " +
            $"{Config.SpiritRoots.Grades[player.SpiritRootGrade].Name}");
        ImGui.Text($"Charm: {Config.CharmTiers[player.CharmTier].Name}   Fortune: {player.Fortune:F0} " +
            $"(x{CultivationRules.FortuneMultiplier(Config, player.Fortune):F2} rewards)");
        ImGui.Text($"Spirit Stones: {player.SpiritStones}   Herbs: {player.Herbs}");
        if (player.InjuryMonths > 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), $"Injured: {player.InjuryMonths} month(s) of halved cultivation");
        }
        var veinBonus = CultivationRules.VeinBonusAt(Config, _runner.Map, player.X, player.Y);
        if (veinBonus > 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.9f, 1f, 1f), $"Standing on a spirit vein: +{veinBonus:P0} cultivation");
        }

        if (_runner.Busy)
        {
            var (remaining, total) = _runner.PendingProgress;
            var fraction = total <= 0 ? 0f : (float)(1.0 - remaining / total);
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"time flows... {remaining:F0} day(s) remain");
        }
        ImGui.End();
    }

    // ---- actions ------------------------------------------------------------------------------

    private void DrawActions()
    {
        ImGui.SetNextWindowPos(new Vector2(40, 380), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(420, 260), ImGuiCond.FirstUseEver);
        ImGui.Begin("Actions");

        var world = _runner.UiWorld;
        ref readonly var cultivator = ref world.GetComponent<Cultivator>(_runner.Player);
        ref readonly var player = ref world.GetComponent<PlayerData>(_runner.Player);

        ImGui.BeginDisabled(_runner.Busy);

        ImGui.SliderInt("months##cultivate", ref _cultivateMonths, 1, 12);
        ImGui.SameLine();
        if (ImGui.Button("Cultivate")) _runner.RequestCultivate(_cultivateMonths);

        ImGui.SliderInt("years##seclude", ref _secludeYears, 1, 100);
        ImGui.SameLine();
        if (ImGui.Button("Seclude")) _runner.RequestSeclude(_secludeYears);

        if (ImGui.Button("Explore the area")) _runner.RequestExplore();

        ImGui.Separator();
        if (CultivationRules.BreakthroughReady(Config, in cultivator))
        {
            var chance = CultivationRules.BreakthroughSuccessChance(Config, _runner.Map, in cultivator, in player);
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.4f, 1f), $"Breakthrough ready: success chance {chance:P0}");
            if (ImGui.Button("Attempt Breakthrough", new Vector2(-1, 0)))
            {
                _runner.RequestBreakthrough();
            }
        }
        else
        {
            ImGui.TextDisabled("Breakthrough: reach Perfected stage with a full cultivation base.");
        }

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
        ImGui.Begin("Location");

        if (tile.SiteIndex < 0)
        {
            ImGui.TextWrapped(tile.Terrain == Terrain.SpiritVein
                ? $"You stand on a quality-{tile.VeinQuality} spirit vein in the wilds. A fine place to cultivate."
                : $"Wilderness ({tile.Terrain}). Nothing but sky and silence.");
            ImGui.End();
            return;
        }

        var site = map.Sites[tile.SiteIndex];
        ImGui.Text($"{(site.Kind == SiteKind.Town ? "Town" : "Sect")}: {site.Name}");
        ImGui.Separator();

        var found = false;
        foreach (var entity in _runner.Npcs)
        {
            ref readonly var npc = ref world.GetComponent<NpcState>(entity);
            if (npc.Alive == 0 || npc.SiteIndex != tile.SiteIndex) continue;
            var realmIndex = world.GetComponent<Cultivator>(entity).RealmIndex;
            var label = $"{CultivationRules.NpcName(Config, in npc)}" +
                $"{(npc.IsLeader != 0 ? " (Sect Leader)" : string.Empty)}: {Config.Realms[realmIndex].Name}";
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
            ImGui.TextDisabled("Select someone to interact with.");
            ImGui.End();
            return;
        }

        ImGui.Separator();
        DrawNpcPanel(_selectedNpc);
        ImGui.End();
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
        ImGui.Text($"Age {npcCultivator.AgeDays / CultivationRules.DaysPerYear(Config):F0} / " +
            $"{Config.Realms[npcCultivator.RealmIndex].LifespanYears:N0}");
        ImGui.Text($"Charm: {Config.CharmTiers[npc.CharmTier].Name}");
        ImGui.EndGroup();

        ImGui.Text($"Their regard for you: {npc.AffectionToPlayer:F0} ({CultivationRules.AffectionTierName(Config, npc.AffectionToPlayer)})");
        ImGui.Text($"Your regard for them: {npc.PlayerAffection:F0} ({CultivationRules.AffectionTierName(Config, npc.PlayerAffection)})");

        ImGui.BeginDisabled(_runner.Busy);
        if (ImGui.Button($"Gift {Config.Interaction.GiftSpiritStones} stones")) _runner.RequestGift(entity);
        ImGui.SameLine();
        if (ImGui.Button("Spar")) _runner.RequestSpar(entity);

        ImGui.SetNextItemWidth(-70);
        var send = ImGui.InputText("##chat", ref _chatInput, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        send |= ImGui.Button("Say");
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
            ImGui.SeparatorText("They remember");
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
        ImGui.Begin("Chronicle");
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
        ImGui.Begin("The Dao Ends");
        var ageYears = cultivator.AgeDays / CultivationRules.DaysPerYear(Config);
        ImGui.TextWrapped($"{CultivationRules.PlayerName(Config, in player)} reached " +
            $"{CultivationRules.RealmTitle(Config, cultivator.RealmIndex, cultivator.SubStage)} and lived " +
            $"{ageYears:F0} years. The chronicle remains.");
        ImGui.Separator();
        if (ImGui.Button("Reincarnate (new fate)", new Vector2(-1, 0)))
        {
            _seedInput = unchecked(_seedInput * 48271 + 7);
            _runner.RequestStartNew(_seedInput, _sizeIndex);
            _hasSelection = false;
            _chatInput = string.Empty;
        }
        ImGui.End();
    }
}
