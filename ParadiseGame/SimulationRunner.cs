using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using ParadiseGame.Audio;
using ParadiseGame.Navigation;
using ParadiseGame.Ui;

namespace ParadiseGame;

/// <summary>A queued "move this entity to here" input from the presentation thread.</summary>
public readonly record struct MoveCommand(Entity Entity, Vector3 Target);

/// <summary>
/// Runs the simulation on its own thread as a sequence of IMMUTABLE ECS-world snapshots, following the
/// double-buffer rules: each tick rents a fresh write-world, reads the current world (read-only) via
/// <c>World.CopyFrom</c>, and writes the new world; a published world is never mutated again. The render
/// thread reads the latest two published worlds and interpolates.
///
/// **Lifetime**: a published snapshot is kept alive as long as the renderer is still reading it. The
/// renderer acquires a pair via <see cref="TrySampleInterpolation"/> (which pins them, releasing the pair
/// it held on the previous call); the sim returns a world to its reuse pool only once the snapshot is
/// outside the interpolation window AND not pinned. So a world is never recycled mid-read, regardless of
/// how far the render thread lags. Single-reader (one render thread).
/// </summary>
public sealed class SimulationRunner : IDisposable
{
    public const double FixedDeltaSeconds = 1.0 / 60.0;
    private const double MaxAccumulatedSeconds = 0.25;
    // Worlds are pre-created on the owner thread (see ctor). Large enough to absorb render-thread stalls
    // that pin snapshots and block recycling; the sim never allocates a world (which would be a
    // cross-thread call to the affinity-guarded SharedWorld.CreateWorld).
    private const int PoolSize = 32;

    private sealed class Snapshot
    {
        public required World World;
        public long Frame; // canonical sim time — the tick index; seconds are derived, drift-free
        public int Pinned; // >0 while the renderer is reading this snapshot
        public double Time => Frame * FixedDeltaSeconds;
    }

    private readonly SharedWorld _shared;
    private readonly INavigationMesh _navigationMesh;
    private readonly Paradise.Physics.CollisionWorld? _collisionWorld;
    private readonly ConcurrentQueue<MoveCommand> _input = new();
    private readonly ConcurrentQueue<(Entity Entity, Vector3 VelocityDelta)> _impulses = new();
    private readonly RewindBuffer _rewind = new();
    private readonly ConcurrentQueue<UiEvent> _uiEvents = new();
    private readonly ConcurrentDictionary<Entity, Vector3> _moveInput = new();
    private readonly object _lock = new();
    private readonly Stopwatch _clock = new();

    // All under _lock (except where noted). _live is oldest→newest; last is the latest ("current").
    private readonly List<Snapshot> _live = new();
    private readonly Stack<World> _pool = new();
    private readonly List<IDisposable> _schedules = new();
    // Snapshot-read execution: the delegate runs the write-world's schedule with the CURRENT
    // (immutable previous-tick) world as the read source for systems' read-only fields.
    private readonly Dictionary<World, Action<World>> _runByWorld = new();
    private long _heldFrameA = -1;
    private long _heldFrameB = -1;

    private long _frame;
    private volatile bool _running;
    private Thread? _thread;
    private volatile Exception? _threadException;
    private bool _disposed;

    public SimulationRunner(INavigationMesh navigationMesh, Paradise.Physics.CollisionWorld? collisionWorld = null)
    {
        _navigationMesh = navigationMesh ?? throw new ArgumentNullException(nameof(navigationMesh));
        _collisionWorld = collisionWorld;
        _shared = SharedWorldFactory.Create();
        // Pre-create the whole world pool on THIS (owner) thread. SharedWorld.CreateWorld/Dispose are the
        // ONLY thread-affinity-guarded ops in Paradise.ECS, so the sim thread must never create a world —
        // it only pops from this pool. Everything else (CopyFrom, CreateEntity, GetComponent, Query,
        // schedule.Run) is affinity-free and safe cross-thread on immutable snapshots.
        for (int i = 0; i < PoolSize; i++)
        {
            _pool.Push(CreateWorldWithSchedule());
        }
        // Initial snapshot (frame 0) — spawn target and first published state.
        _live.Add(new Snapshot { World = RentWorldUnlocked(), Frame = 0 });
    }

    /// <summary>The immutable static collision world (safe to query from any thread), if any.</summary>
    public Paradise.Physics.CollisionWorld? CollisionWorld => _collisionWorld;

    public double Now => _clock.Elapsed.TotalSeconds;
    public bool HasSnapshots { get { lock (_lock) { return _live.Count > 0; } } }
    public double LatestSnapshotTime { get { lock (_lock) { return _live.Count == 0 ? 0 : _live[^1].Time; } } }
    public Exception? ThreadException => _threadException;

    // ---- Init-time spawning (before Start): populate the initial snapshot world ----

    private World Current => _live[^1].World; // sim-thread only; sim is the sole writer of _live

    public Entity SpawnStatic(Vector3 position, Quaternion rotation) =>
        Current.CreateEntity(EntityBuilder.Create().Add(new LocalTransform(position, rotation)));

    public Entity SpawnAgent(Vector3 position, Quaternion rotation, float moveSpeed, float arriveRadius,
        float bodyRadius = 0.4f, float bodyHalfLength = 0.5f) =>
        Current.CreateEntity(EntityBuilder.Create()
            .Add(new LocalTransform(position, rotation))
            .Add(new NavAgent(moveSpeed, arriveRadius))
            .Add(new NavPath())
            .Add(new MoveIntent())
            .Add(new CharacterBody(bodyRadius, bodyHalfLength))
            // Seeded: under snapshot reads, systems see the CURRENT world's SimulationContext
            // (written last tick); seeding removes the one-tick dt warmup on the very first tick.
            .Add(new SimulationContext { DeltaSeconds = (float)FixedDeltaSeconds })
            .Add(new PhysicsWorldRef { Handle = _collisionWorld?.Handle ?? default }));

    /// <summary>Spawn a dynamic physics ball (sphere). Position is the sphere center.</summary>
    public Entity SpawnBall(Vector3 position, Quaternion rotation, float radius, float mass = 1f)
    {
        var ball = Current.CreateEntity(EntityBuilder.Create()
            .Add(new LocalTransform(position, rotation))
            .Add(new DynamicBody(radius, mass))
            .Add(new BallGlow())
            .Add(new SimulationContext { DeltaSeconds = (float)FixedDeltaSeconds })
            .Add(new PhysicsWorldRef { Handle = _collisionWorld?.Handle ?? default }));
        _ballEntities.Add(ball);
        return ball;
    }
    private readonly List<Entity> _ballEntities = new();

    public void EnqueueMoveTo(Entity entity, Vector3 target) => _input.Enqueue(new MoveCommand(entity, target));

    /// <summary>The optional sim-thread UI half. Set before <see cref="Start"/>; every tick the
    /// runner drains queued UI events into it and advances its time — so hover/focus/animations
    /// run in lockstep with game state. The renderer half of the same UI system runs on the
    /// render thread and synchronizes internally with this one.</summary>
    public IUiInput? UiInput { get; set; }

    /// <summary>Invoked ON THE SIM THREAD for pointer-downs the UI did not consume and that
    /// carry a world-space pick ray — the game-side "clicked the world" hook (click-to-move).</summary>
    public Action<UiEvent>? UiUnhandledPointerDown { get; set; }

    /// <summary>Queue a UI event from the platform/render thread; drained on the sim thread
    /// each tick, before movement input, so a click consumed by a UI panel never leaks into
    /// world interaction on the same tick.</summary>
    public void EnqueueUiEvent(in UiEvent uiEvent) => _uiEvents.Enqueue(uiEvent);

    /// <summary>The optional sim-thread audio half (mirror of <see cref="UiInput"/>, data
    /// flowing the other way): game logic posts events/parameters through it on the sim
    /// thread and the runner advances its time each fixed tick. The system's pump half runs
    /// on the render thread.</summary>
    public IAudioSink? Audio { get; set; }

    /// <summary>Set an agent's current direct-move (WASD) direction; applied every tick until changed
    /// (zero = no input). Overrides click-to-move path following while non-zero.</summary>
    public void SetMoveInput(Entity entity, Vector3 direction) => _moveInput[entity] = direction;

    /// <summary>Add a velocity delta to a dynamic ball on its next tick (the pool strike).</summary>
    public void EnqueueBallImpulse(Entity entity, Vector3 velocityDelta) => _impulses.Enqueue((entity, velocityDelta));

    /// <summary>Freeze the fixed-tick loop (rendering keeps interpolating the last published
    /// snapshots). While paused the rewind buffer can be scrubbed and
    /// <see cref="RestoreFromRewind"/> rewrites history.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }
    private volatile bool _paused;

    /// <summary>Number of frames available to scrub backwards (0 = only the present).</summary>
    public int RewindFrameCount => _rewind.Count;

    /// <summary>Read the recorded ball states <paramref name="framesBack"/> frames ago into
    /// <paramref name="states"/> (cleared first) for scrub-time display. Thread-safe; false
    /// when the buffer does not reach that far.</summary>
    public bool TryGetRewindFrame(int framesBack, List<RewoundBall> states) => _rewind.TryGet(framesBack, states);

    /// <summary>Rewrite the present from the frame <paramref name="framesBack"/> frames ago:
    /// published as a NEW snapshot (a restore-tick — immutability of published worlds holds),
    /// with recorded frames after that point discarded. The next ticks then diverge from the
    /// restored state (re-aim the cue, resume, watch a new future). Call while paused. False
    /// when nothing was restored (bad frame, or every world genuinely pinned) — callers must
    /// not treat the rewind as applied.</summary>
    public bool RestoreFromRewind(int framesBack)
    {
        if (framesBack <= 0 || !_rewind.TryGet(framesBack, _restoreScratch)) return false;

        World current;
        World write;
        lock (_lock)
        {
            if (_pool.Count == 0)
            {
                PruneUnlocked(); // same starvation hardening as TickOnce: publish-time pruning
            }                    // is not enough when the renderer holds pins across frames
            if (_pool.Count == 0)
            {
                return false;
            }
            current = _live[^1].World;
            write = _pool.Pop();
        }
        write.CopyFrom(current);
        foreach (var ball in _restoreScratch)
        {
            if (!write.IsAlive(ball.Entity)) continue;
            ref var transform = ref write.GetComponent<LocalTransform>(ball.Entity);
            transform.Position = ball.Position;
            transform.Rotation = ball.Rotation;
            write.GetComponent<DynamicBody>(ball.Entity).Velocity = ball.Velocity;
            write.GetComponent<BallGlow>(ball.Entity).Intensity = ball.Glow;
        }
        _rewind.DropNewest(framesBack);
        lock (_lock)
        {
            _live.Add(new Snapshot { World = write, Frame = ++_frame });
            PruneUnlocked();
        }
        return true;
    }
    private readonly List<RewoundBall> _restoreScratch = new();

    // ---- Threading ----

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("Already started.");
        _running = true;
        _clock.Start();
        _thread = new Thread(Run) { IsBackground = true, Name = "ParadiseSim" };
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
                double now = _clock.Elapsed.TotalSeconds;
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

    /// <summary>Drain queued UI events and advance the UI + audio sinks one fixed step. Runs
    /// on the sim thread, from every world tick AND at the same cadence while PAUSED — pause
    /// freezes the world, not the UI (the pause panel must stay interactive) — so UI time is
    /// a MONOTONIC tick count rather than world time. A click a panel consumes never falls
    /// through to world interaction; unconsumed world clicks enqueue their MoveCommand in time
    /// for the same tick's drain (no-op while paused: the command applies on resume). The
    /// queue drains even with no UiInput attached (events dropped) so a producer without a UI
    /// half can never grow it unbounded.</summary>
    private void PumpUi()
    {
        var ui = UiInput;
        while (_uiEvents.TryDequeue(out var uiEvent))
        {
            var consumed = ui?.Handle(in uiEvent) ?? false;
            if (!consumed && uiEvent is { Kind: UiEventKind.PointerDown, HasWorldRay: true })
            {
                UiUnhandledPointerDown?.Invoke(uiEvent);
            }
        }
        var uiTime = ++_uiTicks * FixedDeltaSeconds;
        ui?.Tick(uiTime);
        Audio?.Tick(uiTime);
    }
    private long _uiTicks;

    // ---- One double-buffered frame (also drives the headless tests synchronously) ----

    public void TickOnce()
    {
        World current;
        World write;
        lock (_lock)
        {
            if (_pool.Count == 0)
            {
                // Publish is normally what prunes, so an empty pool must prune HERE first —
                // otherwise a renderer that released its pins while we were starved could never
                // be noticed (no publish → no prune → no refill: a permanent stall).
                PruneUnlocked();
            }
            if (_pool.Count == 0)
            {
                // Genuinely every world is pinned — a stalled renderer is still reading them
                // all. Skip this tick (backpressure) and retry once pins release.
                return;
            }
            current = _live[^1].World; // read the current snapshot ref under the lock
            write = _pool.Pop();
        }

        // Rules 2 + 3: read current (read-only, immutable), write the new world — outside the lock.
        write.CopyFrom(current);

        SimulationTick.PrepareFrame(write, (float)FixedDeltaSeconds);

        PumpUi();

        while (_input.TryDequeue(out MoveCommand cmd))
        {
            if (write.IsAlive(cmd.Entity))
            {
                NavigationPlanner.PlanMoveTo(write, cmd.Entity, cmd.Target, _navigationMesh);
            }
        }

        while (_impulses.TryDequeue(out var impulse))
        {
            if (write.IsAlive(impulse.Entity) && write.HasComponent<DynamicBody>(impulse.Entity))
            {
                write.GetComponent<DynamicBody>(impulse.Entity).Velocity += impulse.VelocityDelta;
            }
        }

        // Direct (WASD) input — applied before the schedule; it overrides path following because
        // Apply clears HasPath (steering skips) and writes the intent MovementSystem integrates.
        foreach (var kv in _moveInput)
        {
            if (write.IsAlive(kv.Key))
            {
                DirectMover.Apply(write, kv.Key, kv.Value);
            }
        }

        // MovementSystem (steering + character slide + ball dynamics — the sole transform
        // writer) runs inside the schedule. Systems' read-only fields bind to `current` (the
        // immutable previous-tick snapshot) — snapshot-read mode.
        _runByWorld[write](current);

        _rewind.Record(write, _ballEntities);

        lock (_lock)
        {
            _live.Add(new Snapshot { World = write, Frame = ++_frame });
            PruneUnlocked();
        }
    }

    // Recycle snapshots that are older than the interpolation window (keep the 2 newest) AND not pinned by
    // the renderer. Pinned snapshots are kept until released, so a world is never reused mid-read.
    // Unpinned frames recycle from ANYWHERE in the window — a front-only sweep would halt at the
    // first pinned frame and let one long-held pin starve the whole pool (the sim then stalls
    // permanently, because publishing is what prunes). Order of the survivors is preserved.
    private void PruneUnlocked()
    {
        for (int i = _live.Count - 3; i >= 0; i--)
        {
            if (_live[i].Pinned == 0)
            {
                _pool.Push(_live[i].World);
                _live.RemoveAt(i);
            }
        }
    }

    // Sim-thread safe: only pops from the pre-created pool (no cross-thread CreateWorld).
    private World RentWorldUnlocked()
    {
        if (_pool.Count == 0)
        {
            throw new InvalidOperationException(
                $"World pool exhausted ({PoolSize}) — the render thread stalled too long while holding snapshots.");
        }
        return _pool.Pop();
    }

    // Owner-thread only (constructor). Creates a world + its schedule.
    // Snapshot-read execution model ([assembly: SnapshotReadSystems] + [assembly: SingleWriter]):
    // read-only fields bind to the immutable current snapshot, writable fields to the write
    // world, and writes are disjoint — so SnapshotDagScheduler collapses the systems into one
    // wave and ParallelWaveScheduler runs them fully parallel, deterministically.
    private World CreateWorldWithSchedule()
    {
        World world = _shared.CreateWorld();
        var schedule = SystemSchedule.Create(world)
            .AddWorld<MovementSystem>()
            .Build(new SnapshotDagScheduler(), new ParallelWaveScheduler());
        _schedules.Add(schedule);
        _runByWorld[world] = schedule.Run;
        return world;
    }

    // ---- Snapshot sampling for interpolation (single reader) ----

    /// <summary>
    /// Pin and return the two published snapshots bracketing <paramref name="sampleTime"/> plus the
    /// interpolation factor. The pair stays pinned (won't be recycled) until the next call releases it. When
    /// out of range both outputs clamp to one snapshot (alpha 0). False only if no snapshot exists yet.
    /// </summary>
    public bool TrySampleInterpolation(double sampleTime, out World a, out World b, out float alpha)
    {
        lock (_lock)
        {
            // Release the pair pinned by the previous call.
            Unpin(_heldFrameA);
            Unpin(_heldFrameB);
            _heldFrameA = _heldFrameB = -1;

            a = default!;
            b = default!;
            alpha = 0f;
            if (_live.Count == 0) return false;

            Snapshot oldest = _live[0];
            Snapshot latest = _live[^1];
            Snapshot sa, sb;
            if (sampleTime <= oldest.Time) { sa = sb = oldest; }
            else if (sampleTime >= latest.Time) { sa = sb = latest; }
            else
            {
                sa = latest; sb = latest;
                for (int i = _live.Count - 1; i > 0; i--)
                {
                    if (_live[i - 1].Time <= sampleTime && sampleTime < _live[i].Time)
                    {
                        sa = _live[i - 1];
                        sb = _live[i];
                        double span = sb.Time - sa.Time;
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
        foreach (Snapshot s in _live)
        {
            if (s.Frame == frame) { s.Pinned--; return; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        lock (_lock)
        {
            foreach (IDisposable schedule in _schedules) schedule.Dispose();
        }
        _shared.Dispose();
    }
}
