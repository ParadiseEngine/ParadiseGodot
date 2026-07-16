using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ParadiseGame.Ui;

namespace ParadiseCultivation;

/// <summary>A queued player action from any thread, applied on the sim thread.</summary>
public enum CommandKind
{
    None,
    StartNew,
    BeginJourney,
    Travel,
    TravelStep,
    Cultivate,
    Seclude,
    Breakthrough,
    Explore,
    Chat,
    Gift,
    Spar,
    Save,
    Load,
}

public readonly record struct CultivationCommand(CommandKind Kind, int A, int B, Entity Target, string? Text);

/// <summary>
/// The cultivation slice on ParadiseGame's snapshot machinery (the <c>SimulationRunner</c>
/// analog): a 60 Hz sim thread advances the ECS world as a sequence of IMMUTABLE snapshots —
/// each tick rents a write-world from a pre-created pool, <c>CopyFrom</c>s the current one,
/// mutates the copy, and publishes it; published worlds are never mutated again. Readers pin
/// pairs via <see cref="TrySampleInterpolation"/> and a world is recycled only when unpinned
/// and outside the window.
///
/// Game flow: player actions arrive as <see cref="CultivationCommand"/>s and are applied on
/// the sim thread; time-consuming actions start an ANIMATED time advance (game days flow at
/// the config rate). Travel walks a terrain-cost A* path tile by tile as the days tick.
/// Month boundaries settle the player in managed code and the NPC population through
/// <see cref="SettlementSystem"/> (snapshot-read parallel schedule); the managed post-pass
/// turns the system's flags into chronicle entries and replacement spawns. The ImGui UI runs
/// on the sim thread via <see cref="UiInput"/>, reading <see cref="UiWorld"/> directly and
/// enqueueing commands — the BankHeist direct-world-access pattern, no facade.
///
/// Free-text interaction goes through the intelligence-layer seam
/// (<see cref="Proposer"/> → <see cref="ProposalRules"/> validation): proposers only
/// suggest; this runner's rules code is the sole writer of authoritative state, and the
/// deterministic <see cref="TemplateProposer"/> keeps everything working offline.
///
/// Randomness is a serializable <see cref="Pcg32"/> stream, so the versioned JSON save
/// (<see cref="SaveData"/>) captures it exactly and a loaded game continues deterministically.
/// String state lives OUTSIDE the ECS: <see cref="Chronicle"/> and the per-NPC memory logs
/// are sim-thread side stores; names/personalities are config-pool indices on components.
/// The terrain/site <see cref="Map"/> is immutable and thread-agnostic.
/// </summary>
public sealed class CultivationRunner : IDisposable
{
    public const double FixedDeltaSeconds = 1.0 / 60.0;
    private const double MaxAccumulatedSeconds = 0.25;
    // Pre-created on the owner thread (SharedWorld.CreateWorld is thread-affinity-guarded);
    // the sim thread only pops from the pool. Sized to absorb reader stalls that pin snapshots.
    private const int PoolSize = 32;

    private sealed class Snapshot
    {
        public required World World;
        public long Frame;
        public int Pinned;
        public double Time => Frame * FixedDeltaSeconds;
    }

    private readonly SharedWorld _shared;
    private readonly ConcurrentQueue<CultivationCommand> _commands = new();
    private readonly ConcurrentQueue<UiEvent> _uiEvents = new();
    private readonly object _lock = new();
    private readonly Stopwatch _clock = new();

    private readonly List<Snapshot> _live = new();
    private readonly Stack<World> _pool = new();
    private readonly List<IDisposable> _schedules = new();
    private readonly Dictionary<World, Action<World>> _runByWorld = new();
    private long _heldFrameA = -1;
    private long _heldFrameB = -1;
    private long _frame;

    private volatile bool _running;
    private Thread? _thread;
    private volatile Exception? _threadException;
    private bool _disposed;
    private volatile bool _paused;
    private long _uiTicks;

    // ---- game state (sim thread; ctor sets up on the owner thread before Start) ----

    private readonly RealmLadder _ladder;
    private readonly SettlementTuning _tuning;
    private WorldMap _map = null!;
    private Pcg32 _rng = null!;
    private int _nextNpcId;
    private readonly List<Entity> _npcs = new();
    private readonly Dictionary<Entity, List<MemoryEntry>> _memories = new();
    private Entity _player;
    private volatile int _phaseRaw = (int)GamePhase.NewGame;

    private double _dayCursor;   // fractional days; _day is its floor
    private long _day;
    private double _pendingDays;
    private double _pendingTargetCursor; // integer-valued double → exact completion day
    private double _pendingRate;         // game days per real second for this action
    private CommandKind _pendingKind;
    private int _pendingAmount;          // months cultivated / years secluded / travel days
    private TravelPlan? _travelPlan;
    private int _travelStepsDone;
    private double _pendingGained;
    private World? _uiWorld;

    public CultivationConfig Config { get; }

    /// <summary>The intelligence-layer proposer for free-text interaction. Swap in an
    /// LLM-backed implementation later; every proposal is validated by
    /// <see cref="ProposalRules"/> and the default keeps the game fully offline.</summary>
    public INpcInteractionProposer Proposer { get; set; } = new TemplateProposer();

    /// <summary>The immutable generated map — safe from any thread.</summary>
    public WorldMap Map => _map;

    public GamePhase Phase => (GamePhase)_phaseRaw;
    public Exception? ThreadException => _threadException;
    public double Now => _clock.Elapsed.TotalSeconds;
    public bool HasSnapshots { get { lock (_lock) { return _live.Count > 0; } } }
    public double LatestSnapshotTime { get { lock (_lock) { return _live.Count == 0 ? 0 : _live[^1].Time; } } }

    /// <summary>Absolute game day (calendar). Torn-read-safe for host status lines.</summary>
    public long Day => Volatile.Read(ref _day);

    // ---- sim-thread state for the UI panels (which run on the sim thread via UiInput) ----

    /// <summary>The world the UI panels read during their draw (the tick's write world, in
    /// last-published state until this tick's mutations land). SIM THREAD ONLY.</summary>
    public World UiWorld => _uiWorld ?? Current;

    public Entity Player => _player;
    /// <summary>All NPC entities ever spawned (dead ones keep their components + memories).
    /// SIM THREAD ONLY.</summary>
    public IReadOnlyList<Entity> Npcs => _npcs;
    /// <summary>The world/biography log. SIM THREAD ONLY.</summary>
    public List<MemoryEntry> Chronicle { get; } = new();
    /// <summary>An action is animating game time; new time-consuming actions are refused.</summary>
    public bool Busy => _pendingDays > 0;
    /// <summary>Remaining/total days of the animating action (progress display).</summary>
    public (double Remaining, double Total) PendingProgress =>
        (_pendingDays, _pendingKind == CommandKind.None ? 0 : _pendingTotalDays);
    private double _pendingTotalDays;

    /// <summary>The path being traveled (null when not traveling) plus days completed —
    /// the UI interpolates the moving player marker from these. SIM THREAD ONLY.</summary>
    public (TravelPlan? Plan, double DaysDone) ActiveTravel =>
        _pendingKind is CommandKind.Travel or CommandKind.TravelStep && _travelPlan is not null
            ? (_travelPlan, _pendingTotalDays - _pendingDays)
            : (null, 0);

    public string LastActionResult { get; private set; } = string.Empty;
    public string LastReply { get; private set; } = string.Empty;

    public string Today => CultivationRules.FormatDate(Config, _day);

    private MessageTextConfig Msg => Config.Text.Messages;
    private static string F(string template, params object[] args) => CultivationRules.F(template, args);

    public IReadOnlyList<MemoryEntry> MemoriesOf(Entity npc) =>
        _memories.TryGetValue(npc, out var list) ? list : Array.Empty<MemoryEntry>();

    /// <summary>The latest world. Pre-<see cref="Start"/> setup and SYNCHRONOUS tests only —
    /// once the sim thread runs, published worlds are immutable and must not be poked.</summary>
    public World Current => _live[^1].World;

    public CultivationRunner(CultivationConfig config, int seed, int? presetIndex = null)
    {
        Config = config;
        (_ladder, _tuning) = CultivationRules.BakeSettlementData(config);
        _shared = SharedWorldFactory.Create();
        for (var i = 0; i < PoolSize; i++)
        {
            _pool.Push(CreateWorldWithSchedule());
        }
        _live.Add(new Snapshot { World = RentWorldUnlocked(), Frame = 0 });

        StartNewWorld(Current, seed, presetIndex ?? config.World.DefaultPresetIndex);
    }

    // ---- commands (any thread) ----

    public void Enqueue(in CultivationCommand command) => _commands.Enqueue(command);

    public void RequestStartNew(int seed, int presetIndex) =>
        Enqueue(new CultivationCommand(CommandKind.StartNew, seed, presetIndex, default, null));

    public void RequestBeginJourney() =>
        Enqueue(new CultivationCommand(CommandKind.BeginJourney, 0, 0, default, null));

    public void RequestTravel(int x, int y) =>
        Enqueue(new CultivationCommand(CommandKind.Travel, x, y, default, null));

    /// <summary>WASD: step one tile in a cardinal direction (four-direction adjacency).</summary>
    public void RequestTravelStep(int dx, int dy) =>
        Enqueue(new CultivationCommand(CommandKind.TravelStep, dx, dy, default, null));

    public void RequestCultivate(int months) =>
        Enqueue(new CultivationCommand(CommandKind.Cultivate, months, 0, default, null));

    public void RequestSeclude(int years) =>
        Enqueue(new CultivationCommand(CommandKind.Seclude, years, 0, default, null));

    public void RequestBreakthrough() =>
        Enqueue(new CultivationCommand(CommandKind.Breakthrough, 0, 0, default, null));

    public void RequestExplore() =>
        Enqueue(new CultivationCommand(CommandKind.Explore, 0, 0, default, null));

    public void RequestChat(Entity npc, string text) =>
        Enqueue(new CultivationCommand(CommandKind.Chat, 0, 0, npc, text));

    public void RequestGift(Entity npc) =>
        Enqueue(new CultivationCommand(CommandKind.Gift, 0, 0, npc, null));

    public void RequestSpar(Entity npc) =>
        Enqueue(new CultivationCommand(CommandKind.Spar, 0, 0, npc, null));

    public void RequestSave(string path) =>
        Enqueue(new CultivationCommand(CommandKind.Save, 0, 0, default, path));

    public void RequestLoad(string path) =>
        Enqueue(new CultivationCommand(CommandKind.Load, 0, 0, default, path));

    // ---- UI pump (the SimulationRunner contract) ----

    /// <summary>The sim-thread UI half. Set before <see cref="Start"/>; every fixed step the
    /// runner drains queued UI events into it and advances its time — panels therefore run on
    /// the sim thread and may read <see cref="UiWorld"/> and the side stores directly.</summary>
    public IUiInput? UiInput { get; set; }

    public void EnqueueUiEvent(in UiEvent uiEvent) => _uiEvents.Enqueue(uiEvent);

    /// <summary>Freeze the world clock; the UI keeps ticking (pause menus stay interactive).</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    // ---- threading (mirrors SimulationRunner) ----

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("Already started.");
        _running = true;
        _clock.Start();
        _thread = new Thread(Run) { IsBackground = true, Name = "CultivationSim" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1000);
        _thread = null;
    }

    private void Run()
    {
        try
        {
            double accumulator = 0, last = _clock.Elapsed.TotalSeconds;
            while (_running)
            {
                var now = _clock.Elapsed.TotalSeconds;
                accumulator = Math.Min(accumulator + (now - last), MaxAccumulatedSeconds);
                last = now;
                while (accumulator >= FixedDeltaSeconds && _running)
                {
                    if (_paused)
                    {
                        PumpUi(); // pause freezes the WORLD, never the UI
                    }
                    else
                    {
                        TickOnce();
                    }
                    accumulator -= FixedDeltaSeconds;
                }
                Thread.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            _threadException = ex;
            _running = false;
        }
    }

    private void PumpUi()
    {
        var ui = UiInput;
        while (_uiEvents.TryDequeue(out var uiEvent))
        {
            ui?.Handle(in uiEvent);
        }
        ui?.Tick(++_uiTicks * FixedDeltaSeconds);
    }

    // ---- one double-buffered frame (also drives the tests synchronously) ----

    public void TickOnce()
    {
        World current;
        World write;
        lock (_lock)
        {
            if (_pool.Count == 0)
            {
                PruneUnlocked(); // publish normally prunes; a starved pool must prune here
            }
            if (_pool.Count == 0)
            {
                return; // every world pinned by a stalled reader — skip (backpressure)
            }
            current = _live[^1].World;
            write = _pool.Pop();
        }

        write.CopyFrom(current);
        _uiWorld = write;

        // UI first (panels read last-published state and enqueue commands), then commands,
        // then the time advance they may have started, then the settlement schedule.
        PumpUi();
        ProcessCommands(write);
        AdvanceTime(write, out var monthsCrossed, out var firstMonthIndex);
        PrepareFrame(write, monthsCrossed, firstMonthIndex);

        _runByWorld[write](current);

        PostPass(write);

        lock (_lock)
        {
            _live.Add(new Snapshot { World = write, Frame = ++_frame });
            PruneUnlocked();
        }
    }

    private void PrepareFrame(World write, int monthsCrossed, long firstMonthIndex)
    {
        foreach (var data in write.Query(default(SimulationContexts)))
        {
            data.SimulationContext.DeltaSeconds = (float)FixedDeltaSeconds;
            data.SimulationContext.Day = _day;
            data.SimulationContext.MonthsCrossed = monthsCrossed;
            data.SimulationContext.FirstMonthIndex = firstMonthIndex;
            data.SimulationContext.WorldSeed = _map.GenerationSeed;
        }
    }

    // ---- time flow ----

    private void BeginAdvance(CommandKind kind, int totalDays, int amount = 0)
    {
        _pendingKind = kind;
        _pendingTotalDays = totalDays;
        _pendingDays = totalDays;
        _pendingTargetCursor = Math.Floor(_dayCursor) + totalDays; // integer-valued → exact end
        _pendingAmount = amount;
        _pendingGained = 0;
        if (kind is not (CommandKind.Travel or CommandKind.TravelStep))
        {
            _travelPlan = null;
        }
        var flow = Config.Time.Flow;
        _pendingRate = Math.Max(flow.DaysPerSecond, totalDays / Math.Max(0.1f, flow.MaxActionSeconds));
    }

    private void AdvanceTime(World write, out int monthsCrossed, out long firstMonthIndex)
    {
        monthsCrossed = 0;
        firstMonthIndex = 0;
        if (Phase != GamePhase.Playing || _pendingDays <= 0)
        {
            return;
        }

        var step = Math.Min(_pendingRate * FixedDeltaSeconds, _pendingDays);
        _pendingDays -= step;
        var done = _pendingDays <= 1e-9;
        _dayCursor = done ? _pendingTargetCursor : _dayCursor + step;
        if (done) _pendingDays = 0;

        var dayBefore = _day;
        var dayAfter = (long)_dayCursor;
        Volatile.Write(ref _day, dayAfter);

        var monthsBefore = dayBefore / Config.Time.DaysPerMonth;
        var monthsAfter = dayAfter / Config.Time.DaysPerMonth;
        monthsCrossed = (int)(monthsAfter - monthsBefore);
        firstMonthIndex = monthsBefore;

        // Player-side settlement (managed — the player entity carries no NpcState, so the
        // settlement system never touches it): aging always, gains only while cultivating.
        ref var cultivator = ref write.GetComponent<Cultivator>(_player);
        ref var player = ref write.GetComponent<PlayerData>(_player);
        cultivator.AgeDays += (dayAfter - dayBefore);

        // Travel: the marker walks the path as days accumulate — the design's "player is
        // always a small moving character on the map", not teleport-on-arrival.
        if (_travelPlan is { } plan && _pendingKind is CommandKind.Travel or CommandKind.TravelStep)
        {
            var daysDone = _pendingTotalDays - _pendingDays;
            while (_travelStepsDone < plan.Steps.Count &&
                   (done || plan.CumulativeDays[_travelStepsDone] <= daysDone))
            {
                var (sx, sy) = plan.Steps[_travelStepsDone];
                player.X = sx;
                player.Y = sy;
                _travelStepsDone++;
            }
        }

        var cultivating = _pendingKind is CommandKind.Cultivate or CommandKind.Seclude;
        for (var m = 0; m < monthsCrossed; m++)
        {
            if (player.InjuryMonths > 0) player.InjuryMonths--;
            if (cultivating)
            {
                var gain = CultivationRules.MonthlyCultivationGain(Config, _map, in cultivator, in player);
                cultivator.CultivationPoints += gain;
                _pendingGained += gain;
                CultivationRules.AdvanceSubStages(Config, ref cultivator);
            }
        }

        if (cultivator.AgeDays / CultivationRules.DaysPerYear(Config) >= player.LifespanYears)
        {
            _phaseRaw = (int)GamePhase.Dead;
            _pendingDays = 0;
            _pendingKind = CommandKind.None;
            _travelPlan = null;
            Log(F(Msg.DeathLog, CultivationRules.PlayerName(Config, in player),
                $"{cultivator.AgeDays / CultivationRules.DaysPerYear(Config):F0}",
                Config.Realms[cultivator.RealmIndex].Name));
            return;
        }

        if (done)
        {
            CompleteAdvance(in cultivator, in player);
        }
    }

    private void CompleteAdvance(in Cultivator cultivator, in PlayerData player)
    {
        switch (_pendingKind)
        {
            case CommandKind.Travel:
            case CommandKind.TravelStep:
            {
                var site = _map.TileAt(player.X, player.Y).SiteIndex;
                var mode = _travelPlan?.Mode ?? Msg.WalkMode;
                LastActionResult = site >= 0
                    ? F(Msg.ArrivedAtMsg, _map.Sites[site].Name, _pendingAmount, mode)
                    : F(Msg.TraveledMsg, _pendingAmount, mode);
                _travelPlan = null;
                break;
            }
            case CommandKind.Cultivate:
                LastActionResult = F(Msg.CultivateDoneMsg, _pendingAmount, $"{_pendingGained:F0}",
                    CultivationRules.RealmTitle(Config, cultivator.RealmIndex, cultivator.SubStage));
                break;
            case CommandKind.Seclude:
                Log(F(Msg.SecludeLeaveLog, CultivationRules.PlayerName(Config, in player), _pendingAmount));
                LastActionResult = F(Msg.SecludeDoneMsg, _pendingAmount, $"{_pendingGained:F0}",
                    CultivationRules.RealmTitle(Config, cultivator.RealmIndex, cultivator.SubStage));
                break;
        }
        _pendingKind = CommandKind.None;
    }

    // ---- command application (sim thread) ----

    private void ProcessCommands(World write)
    {
        while (_commands.TryDequeue(out var command))
        {
            switch (command.Kind)
            {
                case CommandKind.StartNew:
                    StartNewWorld(write, command.A, Math.Clamp(command.B, 0, Config.World.Presets.Length - 1));
                    continue;
                case CommandKind.BeginJourney:
                    if (Phase == GamePhase.NewGame) _phaseRaw = (int)GamePhase.Playing;
                    continue;
                case CommandKind.Load:
                    if (!Busy) LoadGame(write, command.Text ?? string.Empty);
                    else LastActionResult = Msg.LoadBusyMsg;
                    continue;
            }

            if (Phase != GamePhase.Playing)
            {
                continue;
            }

            if (command.Kind == CommandKind.Save)
            {
                if (!Busy) SaveGame(write, command.Text ?? string.Empty);
                else LastActionResult = Msg.SaveBusyMsg;
                continue;
            }

            if (Busy)
            {
                LastActionResult = Msg.Occupied;
                continue;
            }

            ref var cultivator = ref write.GetComponent<Cultivator>(_player);
            ref var player = ref write.GetComponent<PlayerData>(_player);

            switch (command.Kind)
            {
                case CommandKind.Travel:
                    BeginTravel(in cultivator, in player, command.A, command.B);
                    break;
                case CommandKind.TravelStep:
                {
                    var dx = Math.Clamp(command.A, -1, 1);
                    var dy = Math.Clamp(command.B, -1, 1);
                    if (dx != 0 && dy != 0) break; // four-direction adjacency only
                    BeginTravel(in cultivator, in player, player.X + dx, player.Y + dy, step: true);
                    break;
                }
                case CommandKind.Cultivate:
                {
                    var months = Math.Max(1, command.A);
                    LastActionResult = F(Msg.CultivateStartMsg, months);
                    BeginAdvance(CommandKind.Cultivate, months * Config.Time.DaysPerMonth, months);
                    break;
                }
                case CommandKind.Seclude:
                {
                    var years = Math.Max(1, command.A);
                    Log(F(Msg.SecludeEnterLog, CultivationRules.PlayerName(Config, in player), years));
                    LastActionResult = F(Msg.SecludeStartMsg, years);
                    BeginAdvance(CommandKind.Seclude, (int)(years * CultivationRules.DaysPerYear(Config)), years);
                    break;
                }
                case CommandKind.Breakthrough:
                    ApplyBreakthrough(ref cultivator, ref player);
                    break;
                case CommandKind.Explore:
                    ApplyExplore(ref cultivator, ref player);
                    break;
                case CommandKind.Chat:
                    ApplyChat(write, command.Target, command.Text ?? string.Empty, ref player, in cultivator);
                    break;
                case CommandKind.Gift:
                    ApplyGift(write, command.Target, ref player);
                    break;
                case CommandKind.Spar:
                    ApplySpar(write, command.Target, ref cultivator, ref player);
                    break;
            }
        }
    }

    private void BeginTravel(in Cultivator cultivator, in PlayerData player, int toX, int toY, bool step = false)
    {
        if (!_map.InBounds(toX, toY) || (toX == player.X && toY == player.Y))
        {
            return;
        }

        var plan = Pathfinding.Plan(Config, _map, player.X, player.Y, cultivator.RealmIndex, toX, toY);
        if (plan is null)
        {
            LastActionResult = step ? Msg.TravelStepBlocked : Msg.TravelNoPath;
            return;
        }

        _travelPlan = plan;
        _travelStepsDone = 0;
        if (!step)
        {
            LastActionResult = F(Msg.TravelStartMsg, plan.Mode, plan.TotalDays);
        }
        BeginAdvance(step ? CommandKind.TravelStep : CommandKind.Travel, plan.TotalDays, plan.TotalDays);
    }

    private void ApplyBreakthrough(ref Cultivator cultivator, ref PlayerData player)
    {
        if (!CultivationRules.BreakthroughReady(Config, in cultivator))
        {
            LastActionResult = Msg.BreakthroughNotReady;
            return;
        }

        var realm = Config.Realms[cultivator.RealmIndex];
        if (_rng.NextDouble() < CultivationRules.BreakthroughSuccessChance(Config, _map, in cultivator, in player))
        {
            cultivator.RealmIndex++;
            cultivator.SubStage = 0;
            cultivator.CultivationPoints = 0;
            player.LifespanYears = Config.Realms[cultivator.RealmIndex].LifespanYears;
            var tribulation = realm.HasTribulation ? Msg.TribulationFlavor : string.Empty;
            Log(F(Msg.BreakthroughLog, CultivationRules.PlayerName(Config, in player),
                Config.Realms[cultivator.RealmIndex].Name, tribulation));
            LastActionResult = F(Msg.BreakthroughMsg,
                CultivationRules.RealmTitle(Config, cultivator.RealmIndex, 0), tribulation,
                $"{player.LifespanYears:N0}");
        }
        else
        {
            cultivator.CultivationPoints *= 1.0 - realm.FailureCultivationLoss;
            player.InjuryMonths += realm.FailureInjuryMonths;
            Log(F(Msg.BreakthroughFailLog, CultivationRules.PlayerName(Config, in player),
                Config.Realms[cultivator.RealmIndex + 1].Name));
            LastActionResult = realm.FailureInjuryMonths > 0
                ? F(Msg.BreakthroughFailInjuryMsg, realm.FailureInjuryMonths)
                : Msg.BreakthroughFailMsg;
        }
        BeginAdvance(CommandKind.Breakthrough, Config.Time.ActionDays.Breakthrough);
    }

    private void ApplyExplore(ref Cultivator cultivator, ref PlayerData player)
    {
        var explore = Config.Player.Explore;
        var multiplier = CultivationRules.FortuneMultiplier(Config, player.Fortune);
        var found = new List<string>();

        if (_rng.NextDouble() < explore.HerbChance)
        {
            var herbs = (int)Math.Round(_rng.Next(explore.HerbMin, explore.HerbMax + 1) * multiplier);
            player.Herbs += herbs;
            found.Add(F(Msg.ExploreHerbs[_rng.Next(Msg.ExploreHerbs.Length)], herbs));
        }
        if (_rng.NextDouble() < explore.StonesChance)
        {
            var stones = (int)Math.Round(_rng.Next(explore.StonesMin, explore.StonesMax + 1) * multiplier);
            player.SpiritStones += stones;
            found.Add(F(Msg.ExploreStones[_rng.Next(Msg.ExploreStones.Length)], stones));
        }
        if (_rng.NextDouble() < explore.InsightChance)
        {
            var points = explore.InsightPoints * Config.SpiritRoots.Grades[player.SpiritRootGrade].Multiplier;
            cultivator.CultivationPoints += points;
            CultivationRules.AdvanceSubStages(Config, ref cultivator);
            player.Fortune = Math.Min(player.Fortune + Config.Fortune.InsightGain, Config.Fortune.Max);
            found.Add(F(Msg.ExploreInsight[_rng.Next(Msg.ExploreInsight.Length)], $"{points:F0}"));
            Log(F(Msg.ExploreInsightLog, CultivationRules.PlayerName(Config, in player)));
        }

        LastActionResult = found.Count == 0
            ? Msg.ExploreNothing[_rng.Next(Msg.ExploreNothing.Length)]
            : F(Msg.ExploreFoundMsg, string.Join(Msg.ExploreListSeparator, found));
        BeginAdvance(CommandKind.Explore, Config.Time.ActionDays.Explore);
    }

    /// <summary>Positive gains scale with charm (never the negative ones, per the design doc)
    /// and are clamped to the affection table's range.</summary>
    private void GainAffection(ref NpcState npc, in PlayerData player, float baseAmount)
    {
        var amount = baseAmount > 0
            ? baseAmount * Config.CharmTiers[player.CharmTier].Multiplier
            : baseAmount;
        npc.AffectionToPlayer = Math.Clamp(npc.AffectionToPlayer + amount, -500f, 1000f);
        npc.PlayerAffection = Math.Clamp(
            npc.PlayerAffection + amount * Config.Interaction.PlayerAffectionShare, -500f, 1000f);
    }

    private bool TryGetLivingNpc(World write, Entity target, out Entity npc)
    {
        npc = target;
        return write.IsAlive(target) && write.HasComponent<NpcState>(target) &&
               write.GetComponent<NpcState>(target).Alive != 0;
    }

    private void ApplyChat(World write, Entity target, string text, ref PlayerData player, in Cultivator cultivator)
    {
        if (string.IsNullOrWhiteSpace(text) || !TryGetLivingNpc(write, target, out var entity)) return;

        ref var npc = ref write.GetComponent<NpcState>(entity);
        var memories = _memories[entity];
        var context = new DialogueContext(
            CultivationRules.NpcName(Config, in npc),
            Config.Npc.Personalities[npc.PersonalityIndex],
            write.GetComponent<Cultivator>(entity).RealmIndex,
            npc.AffectionToPlayer,
            memories.Count,
            npc.NpcId,
            CultivationRules.PlayerName(Config, in player),
            cultivator.RealmIndex);

        // Intelligence layer proposes; the rules layer validates and applies. The proposer's
        // affection suggestion is clamped to the budget, then subjected to the same rules as
        // ever: diminishing monthly returns and charm scaling on the positive side.
        var proposal = Proposer.Propose(Config, in context, text);
        var suggested = ProposalRules.ClampAffectionDelta(Config, proposal.AffectionDeltaSuggestion);
        GainAffection(ref npc, in player, suggested / (1f + npc.ChatsThisMonth));
        npc.ChatsThisMonth++;

        LastReply = ProposalRules.SanitizeReply(Config, proposal.ReplyText, Msg.ChatFallbackReply);
        var summary = ProposalRules.SanitizeReply(
            Config, proposal.MemorySummary, F(Msg.ChatMemory, context.PlayerName, Truncate(text, 60)));
        memories.Add(new MemoryEntry(_day, summary));
        BeginAdvance(CommandKind.Chat, Config.Time.ActionDays.Chat);
    }

    private void ApplyGift(World write, Entity target, ref PlayerData player)
    {
        if (!TryGetLivingNpc(write, target, out var entity)) return;

        var stones = Config.Interaction.GiftSpiritStones;
        if (player.SpiritStones < stones)
        {
            LastReply = F(Msg.GiftNeedMsg, stones);
            return;
        }

        player.SpiritStones -= stones;
        ref var npc = ref write.GetComponent<NpcState>(entity);
        GainAffection(ref npc, in player, stones * Config.Interaction.GiftAffectionPerStone);
        _memories[entity].Add(new MemoryEntry(
            _day, F(Msg.GiftMemory, CultivationRules.PlayerName(Config, in player), stones)));
        LastReply = F(Msg.GiftReply, CultivationRules.NpcName(Config, in npc), stones,
            CultivationRules.AffectionTierName(Config, npc.AffectionToPlayer));
        BeginAdvance(CommandKind.Gift, Config.Time.ActionDays.Gift);
    }

    private void ApplySpar(World write, Entity target, ref Cultivator cultivator, ref PlayerData player)
    {
        if (!TryGetLivingNpc(write, target, out var entity)) return;

        var cfg = Config.Interaction;
        ref var npc = ref write.GetComponent<NpcState>(entity);
        ref var npcCultivator = ref write.GetComponent<Cultivator>(entity);

        var playerPower = cultivator.RealmIndex * cfg.SparPowerPerRealm + cultivator.SubStage * cfg.SparPowerPerSubStage
            + (float)(_rng.NextDouble() * cfg.SparRollSpread);
        var npcPower = npcCultivator.RealmIndex * cfg.SparPowerPerRealm + npcCultivator.SubStage * cfg.SparPowerPerSubStage
            + (float)(_rng.NextDouble() * cfg.SparRollSpread);
        var won = playerPower >= npcPower;

        // Sparring is the no-consequence combat type: both sides gain respect either way.
        GainAffection(ref npc, in player, won ? cfg.SparWinAffection : cfg.SparLoseAffection);
        if (won)
        {
            cultivator.CultivationPoints += cfg.SparInsightPoints;
            CultivationRules.AdvanceSubStages(Config, ref cultivator);
        }
        var playerName = CultivationRules.PlayerName(Config, in player);
        _memories[entity].Add(new MemoryEntry(_day, won
            ? F(Msg.SparWinMemory, playerName)
            : F(Msg.SparLoseMemory, playerName)));
        LastReply = won
            ? F(Msg.SparWinReply, CultivationRules.NpcName(Config, in npc), cfg.SparInsightPoints)
            : F(Msg.SparLoseReply, CultivationRules.NpcName(Config, in npc));
        BeginAdvance(CommandKind.Spar, Config.Time.ActionDays.Spar);
    }

    // ---- save / load (versioned JSON; corrupt loads must fail safely) ----

    private void SaveGame(World write, string path)
    {
        try
        {
            var data = BuildSaveData(write);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(data, CultivationJsonContext.Default.SaveData));
            LastActionResult = F(Msg.SaveDoneMsg, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            LastActionResult = F(Msg.SaveFailMsg, e.Message);
        }
    }

    private SaveData BuildSaveData(World write)
    {
        var npcs = new SavedNpc[_npcs.Count];
        for (var i = 0; i < _npcs.Count; i++)
        {
            var entity = _npcs[i];
            var npc = write.GetComponent<NpcState>(entity);
            var cultivator = write.GetComponent<Cultivator>(entity);
            npcs[i] = new SavedNpc
            {
                Cultivator = ToSaved(in cultivator),
                NpcId = npc.NpcId,
                SiteIndex = npc.SiteIndex,
                SurnameIndex = npc.SurnameIndex,
                GivenNameIndex = npc.GivenNameIndex,
                PersonalityIndex = npc.PersonalityIndex,
                CharmTier = npc.CharmTier,
                AffectionToPlayer = npc.AffectionToPlayer,
                PlayerAffection = npc.PlayerAffection,
                ChatsThisMonth = npc.ChatsThisMonth,
                Alive = npc.Alive != 0,
                IsLeader = npc.IsLeader != 0,
                Memories = _memories[entity].Select(m => new SavedMemory { Day = m.Day, Summary = m.Summary }).ToArray(),
            };
        }

        var player = write.GetComponent<PlayerData>(_player);
        var playerCultivator = write.GetComponent<Cultivator>(_player);
        return new SaveData
        {
            Version = SaveData.CurrentVersion,
            Seed = _map.Seed,
            PresetIndex = _map.PresetIndex,
            Day = _day,
            DayCursor = _dayCursor,
            Phase = Phase,
            RngState = _rng.State,
            RngStream = _rng.Stream,
            NextNpcId = _nextNpcId,
            Player = new SavedPlayer
            {
                Cultivator = ToSaved(in playerCultivator),
                SurnameIndex = player.SurnameIndex,
                GivenNameIndex = player.GivenNameIndex,
                SpiritRootElement = player.SpiritRootElement,
                SpiritRootGrade = player.SpiritRootGrade,
                CharmTier = player.CharmTier,
                X = player.X,
                Y = player.Y,
                Fortune = player.Fortune,
                SpiritStones = player.SpiritStones,
                Herbs = player.Herbs,
                InjuryMonths = player.InjuryMonths,
                LifespanYears = player.LifespanYears,
            },
            Npcs = npcs,
            Chronicle = Chronicle.Select(m => new SavedMemory { Day = m.Day, Summary = m.Summary }).ToArray(),
        };

        static SavedCultivator ToSaved(in Cultivator c) => new()
        {
            RealmIndex = c.RealmIndex,
            SubStage = c.SubStage,
            CultivationPoints = c.CultivationPoints,
            AgeDays = c.AgeDays,
        };
    }

    private void LoadGame(World write, string path)
    {
        SaveData data;
        try
        {
            data = JsonSerializer.Deserialize(File.ReadAllText(path), CultivationJsonContext.Default.SaveData)
                ?? throw new JsonException("save deserialized to null");
            if (data.Version != SaveData.CurrentVersion)
            {
                LastActionResult = F(Msg.LoadVersionMsg, data.Version, SaveData.CurrentVersion);
                return;
            }
            if (data.PresetIndex < 0 || data.PresetIndex >= Config.World.Presets.Length ||
                data.Npcs is null || data.Player is null || data.Chronicle is null)
            {
                LastActionResult = Msg.LoadMalformedMsg;
                return;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fail safely: the running world is untouched.
            LastActionResult = F(Msg.LoadFailMsg, e.Message);
            return;
        }

        // The map re-derives from the seed (same-seed reproducibility); dynamic state loads.
        var generated = WorldGenerator.Generate(Config, data.Seed, data.PresetIndex);
        _map = generated.Map;
        _rng = new Pcg32(data.RngState, data.RngStream);
        _nextNpcId = data.NextNpcId;
        _npcs.Clear();
        _memories.Clear();
        Chronicle.Clear();
        foreach (var entry in data.Chronicle)
        {
            Chronicle.Add(new MemoryEntry(entry.Day, entry.Summary));
        }
        _dayCursor = data.DayCursor;
        Volatile.Write(ref _day, data.Day);
        _pendingDays = 0;
        _pendingKind = CommandKind.None;
        _travelPlan = null;
        LastReply = string.Empty;

        write.Clear();
        foreach (var saved in data.Npcs)
        {
            var entity = write.CreateEntity(EntityBuilder.Create()
                .Add(new Cultivator
                {
                    RealmIndex = saved.Cultivator.RealmIndex,
                    SubStage = saved.Cultivator.SubStage,
                    CultivationPoints = saved.Cultivator.CultivationPoints,
                    AgeDays = saved.Cultivator.AgeDays,
                })
                .Add(new NpcState
                {
                    NpcId = saved.NpcId,
                    SiteIndex = saved.SiteIndex,
                    SurnameIndex = saved.SurnameIndex,
                    GivenNameIndex = saved.GivenNameIndex,
                    PersonalityIndex = saved.PersonalityIndex,
                    CharmTier = saved.CharmTier,
                    AffectionToPlayer = saved.AffectionToPlayer,
                    PlayerAffection = saved.PlayerAffection,
                    ChatsThisMonth = saved.ChatsThisMonth,
                    Alive = (byte)(saved.Alive ? 1 : 0),
                    IsLeader = (byte)(saved.IsLeader ? 1 : 0),
                })
                .Add(new SimulationContext { DeltaSeconds = (float)FixedDeltaSeconds, Day = _day, WorldSeed = _map.GenerationSeed })
                .Add(_ladder)
                .Add(_tuning));
            _npcs.Add(entity);
            _memories[entity] = saved.Memories.Select(m => new MemoryEntry(m.Day, m.Summary)).ToList();
        }

        _player = write.CreateEntity(EntityBuilder.Create()
            .Add(new Cultivator
            {
                RealmIndex = data.Player.Cultivator.RealmIndex,
                SubStage = data.Player.Cultivator.SubStage,
                CultivationPoints = data.Player.Cultivator.CultivationPoints,
                AgeDays = data.Player.Cultivator.AgeDays,
            })
            .Add(new PlayerData
            {
                SurnameIndex = data.Player.SurnameIndex,
                GivenNameIndex = data.Player.GivenNameIndex,
                SpiritRootElement = data.Player.SpiritRootElement,
                SpiritRootGrade = data.Player.SpiritRootGrade,
                CharmTier = data.Player.CharmTier,
                X = Math.Clamp(data.Player.X, 0, _map.Width - 1),
                Y = Math.Clamp(data.Player.Y, 0, _map.Height - 1),
                Fortune = data.Player.Fortune,
                SpiritStones = data.Player.SpiritStones,
                Herbs = data.Player.Herbs,
                InjuryMonths = data.Player.InjuryMonths,
                LifespanYears = data.Player.LifespanYears,
            })
            .Add(new SimulationContext { DeltaSeconds = (float)FixedDeltaSeconds, Day = _day, WorldSeed = _map.GenerationSeed }));

        _phaseRaw = (int)data.Phase;
        LastActionResult = F(Msg.LoadDoneMsg, path);
    }

    // ---- post-pass: turn system flags into chronicle entries + replacement spawns ----

    private void PostPass(World write)
    {
        // Structural changes wait until after the flag scan (spawning while holding component
        // refs would be fragile even though chunk blocks never move).
        _replacementScratch.Clear();
        foreach (var entity in _npcs)
        {
            ref var npc = ref write.GetComponent<NpcState>(entity);

            if (npc.JustBrokeThrough != 0)
            {
                npc.JustBrokeThrough = 0;
                var realmIndex = write.GetComponent<Cultivator>(entity).RealmIndex;
                if (realmIndex >= Config.Npc.NotableRealmIndex)
                {
                    Log(F(Msg.NpcBreakthroughLog, CultivationRules.NpcName(Config, in npc),
                        _map.Sites[npc.SiteIndex].Name, Config.Realms[realmIndex].Name));
                }
            }

            if (npc.JustDied != 0)
            {
                npc.JustDied = 0;
                var realmIndex = write.GetComponent<Cultivator>(entity).RealmIndex;
                if (realmIndex >= Config.Npc.NotableRealmIndex || npc.IsLeader != 0)
                {
                    Log(F(Msg.NpcDeathLog, CultivationRules.NpcName(Config, in npc),
                        _map.Sites[npc.SiteIndex].Name));
                }
                _replacementScratch.Add(WorldGenerator.CreateNpcSpec(
                    Config, _rng, _nextNpcId++, _map, npc.SiteIndex, npc.IsLeader != 0,
                    Config.Npc.ReplacementAgeYears));
            }
        }

        foreach (var replacement in _replacementScratch)
        {
            SpawnNpc(write, in replacement);
        }
    }
    private readonly List<NpcSpec> _replacementScratch = new();

    // ---- world (re)construction ----

    /// <summary>(Re)generate the world and a fresh character into <paramref name="world"/> —
    /// the design doc's "reroll until satisfied". Deterministic per (seed, presetIndex).</summary>
    private void StartNewWorld(World world, int seed, int presetIndex)
    {
        var generated = WorldGenerator.Generate(Config, seed, presetIndex);
        _map = generated.Map;
        _rng = new Pcg32(seed * 31 + 17);
        _nextNpcId = generated.Npcs.Count + 1;
        _npcs.Clear();
        _memories.Clear();
        Chronicle.Clear();
        LastActionResult = string.Empty;
        LastReply = string.Empty;
        _dayCursor = 0;
        Volatile.Write(ref _day, 0);
        _pendingDays = 0;
        _pendingKind = CommandKind.None;
        _travelPlan = null;

        world.Clear();
        foreach (var spec in generated.Npcs)
        {
            SpawnNpc(world, in spec);
        }
        _player = SpawnPlayer(world, seed);
        _phaseRaw = (int)GamePhase.NewGame;

        var home = _map.Sites.FirstOrDefault(s => s.Kind == SiteKind.Town);
        var playerName = CultivationRules.PlayerName(Config, world.GetComponent<PlayerData>(_player));
        var homeName = home?.Name ?? Msg.WildsName;
        Log(F(Msg.ArrivalLog, playerName, homeName));
        // The onboarding guidance beat: a short main-storyline opening in the chronicle.
        foreach (var line in Config.Text.Intro)
        {
            Log(F(line, playerName, homeName));
        }
    }

    private Entity SpawnNpc(World world, in NpcSpec spec)
    {
        var entity = world.CreateEntity(EntityBuilder.Create()
            .Add(new Cultivator
            {
                RealmIndex = spec.RealmIndex,
                SubStage = spec.SubStage,
                AgeDays = spec.AgeDays,
            })
            .Add(new NpcState
            {
                NpcId = spec.NpcId,
                SiteIndex = spec.SiteIndex,
                SurnameIndex = spec.SurnameIndex,
                GivenNameIndex = spec.GivenNameIndex,
                PersonalityIndex = spec.PersonalityIndex,
                CharmTier = spec.CharmTier,
                Alive = 1,
                IsLeader = (byte)(spec.IsLeader ? 1 : 0),
            })
            // Seeded like SimulationRunner.SpawnAgent: under snapshot reads, read-only fields
            // see the CURRENT world's values, so the spawn tick must not read zeros.
            .Add(new SimulationContext { DeltaSeconds = (float)FixedDeltaSeconds, Day = _day, WorldSeed = _map.GenerationSeed })
            .Add(_ladder)
            .Add(_tuning));
        _npcs.Add(entity);
        _memories[entity] = new List<MemoryEntry>();
        return entity;
    }

    private Entity SpawnPlayer(World world, int seed)
    {
        var rng = new SystemRng(seed ^ 0x0C171A7E);
        var home = _map.Sites.FirstOrDefault(s => s.Kind == SiteKind.Town);
        return world.CreateEntity(EntityBuilder.Create()
            .Add(new Cultivator
            {
                RealmIndex = 0,
                SubStage = 0,
                AgeDays = (double)Config.Time.StartAgeYears * CultivationRules.DaysPerYear(Config),
            })
            .Add(new PlayerData
            {
                SurnameIndex = rng.Next(Config.Names.Surnames.Length),
                GivenNameIndex = rng.Next(Config.Names.GivenNames.Length),
                SpiritRootElement = rng.Next(Config.SpiritRoots.Elements.Length),
                SpiritRootGrade = WorldGenerator.RollWeighted(rng, Config.SpiritRoots.Grades, static g => g.Weight),
                CharmTier = WorldGenerator.RollWeighted(rng, Config.CharmTiers, static t => t.Weight),
                X = home?.X ?? _map.Width / 2,
                Y = home?.Y ?? _map.Height / 2,
                Fortune = Config.Fortune.Initial,
                SpiritStones = Config.Player.StartSpiritStones,
                LifespanYears = Config.Realms[0].LifespanYears,
            })
            .Add(new SimulationContext { DeltaSeconds = (float)FixedDeltaSeconds, Day = _day, WorldSeed = _map.GenerationSeed }));
    }

    private void Log(string text) => Chronicle.Add(new MemoryEntry(_day, text));

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    // ---- snapshot pool + sampling (verbatim SimulationRunner mechanics) ----

    private void PruneUnlocked()
    {
        for (var i = _live.Count - 3; i >= 0; i--)
        {
            if (_live[i].Pinned == 0)
            {
                _pool.Push(_live[i].World);
                _live.RemoveAt(i);
            }
        }
    }

    private World RentWorldUnlocked()
    {
        if (_pool.Count == 0)
        {
            throw new InvalidOperationException(
                $"World pool exhausted ({PoolSize}) — a reader stalled too long while holding snapshots.");
        }
        return _pool.Pop();
    }

    private World CreateWorldWithSchedule()
    {
        var world = _shared.CreateWorld();
        var schedule = SystemSchedule.Create(world)
            .Add<SettlementSystem>()
            .Build(new SnapshotDagScheduler(), new ParallelWaveScheduler());
        _schedules.Add(schedule);
        _runByWorld[world] = schedule.Run;
        return world;
    }

    /// <summary>
    /// Pin and return the two published snapshots bracketing <paramref name="sampleTime"/> plus
    /// the interpolation factor. The pair stays pinned (never recycled) until the next call
    /// releases it. Out of range clamps both to one snapshot (alpha 0). Single reader.
    /// </summary>
    public bool TrySampleInterpolation(double sampleTime, out World a, out World b, out float alpha)
    {
        lock (_lock)
        {
            Unpin(_heldFrameA);
            Unpin(_heldFrameB);
            _heldFrameA = _heldFrameB = -1;

            a = default!;
            b = default!;
            alpha = 0f;
            if (_live.Count == 0) return false;

            var oldest = _live[0];
            var latest = _live[^1];
            Snapshot sa, sb;
            if (sampleTime <= oldest.Time) { sa = sb = oldest; }
            else if (sampleTime >= latest.Time) { sa = sb = latest; }
            else
            {
                sa = latest;
                sb = latest;
                for (var i = _live.Count - 1; i > 0; i--)
                {
                    if (_live[i - 1].Time <= sampleTime && sampleTime < _live[i].Time)
                    {
                        sa = _live[i - 1];
                        sb = _live[i];
                        var span = sb.Time - sa.Time;
                        alpha = span <= 0 ? 0f : (float)((sampleTime - sa.Time) / span);
                        break;
                    }
                }
            }

            sa.Pinned++;
            sb.Pinned++;
            _heldFrameA = sa.Frame;
            _heldFrameB = sb.Frame;
            a = sa.World;
            b = sb.World;
            return true;
        }
    }

    private void Unpin(long frame)
    {
        if (frame < 0) return;
        foreach (var snapshot in _live)
        {
            if (snapshot.Frame == frame)
            {
                snapshot.Pinned--;
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        lock (_lock)
        {
            foreach (var schedule in _schedules) schedule.Dispose();
        }
        _shared.Dispose();
    }
}
