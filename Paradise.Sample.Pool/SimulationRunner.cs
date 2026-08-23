using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Paradise.Physics;
using Paradise.Sample.Pool.Audio;
using Paradise.Ui;
using Paradise.Windowing;

namespace Paradise.Sample.Pool;

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
    private readonly Paradise.Physics.CollisionWorld? _collisionWorld;
    private readonly ConcurrentQueue<(Entity Entity, Vector3 VelocityDelta, Vector3? Angular)> _impulses = new();
    private readonly RewindBuffer _rewind = new();
    private readonly ConcurrentQueue<WorldPointerEvent> _uiEvents = new();
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

    public SimulationRunner(Paradise.Physics.CollisionWorld? collisionWorld = null)
    {
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
        // The single score entity for the ScoreSystem reactor demo (fed only by the SystemEvents bus).
        // Created on the initial snapshot; CopyFrom carries it into every later tick's world.
        _scoreEntity = Current.CreateEntity(EntityBuilder.Create().Add(new Score()));
    }

    // The single score entity (ScoreSystem reactor demo). Sim-thread-created; read under _lock.
    private Entity _scoreEntity;

    // Monotonic id handed to each spawned ball so a BallPocketed event can name the ball that dropped.
    private int _nextBallId;

    // Set by RequestReset (any thread), consumed on the sim thread in TickOnce → GameReset emit.
    private volatile bool _resetRequested;

    /// <summary>The immutable static collision world (safe to query from any thread), if any.</summary>
    public Paradise.Physics.CollisionWorld? CollisionWorld => _collisionWorld;

    public double Now => _clock.Elapsed.TotalSeconds;
    public bool HasSnapshots { get { lock (_lock) { return _live.Count > 0; } } }
    public double LatestSnapshotTime { get { lock (_lock) { return _live.Count == 0 ? 0 : _live[^1].Time; } } }
    public Exception? ThreadException => _threadException;

    // ---- Init-time spawning (before Start): populate the initial snapshot world ----

    private World Current => _live[^1].World; // sim-thread only; sim is the sole writer of _live

    public Entity SpawnStatic(Vector3 position, Quaternion rotation) =>
        Current.CreateEntity(EntityBuilder.Create()
            .Add(new Position { Value = position })
            .Add(new Rotation { Value = rotation }));

    /// <summary>Spawn a dynamic physics ball (sphere). Position is the sphere center.
    /// <paramref name="pocket"/> carries the optional pool-game config (pockets, tray slot,
    /// cue respawn); the default is inert — the ball never sinks. <paramref name="tuning"/>
    /// carries the scene's global solver tuning (data/ProjectSettings.json); null = defaults.</summary>
    public Entity SpawnBall(Vector3 position, Quaternion rotation, float radius, float mass = 1f,
        float linearDamping = 1.5f, float restitution = 0.6f, float staticRestitution = 0.4f,
        in PocketConfig pocket = default, PhysicsTuning? tuning = null,
        float friction = 0.3f, float angularDamping = 0.4f)
    {
        var ball = Current.CreateEntity(EntityBuilder.Create()
            .Add(new Position { Value = position })
            .Add(new Rotation { Value = rotation })
            .Add(new Velocity())
            .Add(new AngularVelocity())
            .Add(new BallPhysicsConfig(radius, mass, linearDamping, restitution, staticRestitution, friction, angularDamping))
            .Add(new BallGlow())
            .Add(new BallSunk())
            .Add(new BallSinking())
            .Add(new SinkTargetY())
            .Add(new BallId { Value = _nextBallId++ })
            .Add(pocket)
            .Add(tuning ?? PhysicsTuning.Default)
            .Add(new SimulationContext { DeltaSeconds = (float)FixedDeltaSeconds })
            .Add(new PhysicsWorldRef { Handle = _collisionWorld?.Handle ?? default }));
        _ballEntities.Add(ball);
        return ball;
    }
    private readonly List<Entity> _ballEntities = new();

    /// <summary>Spawn a flipbook 2D-animation clock (a placed sprite). The sim owns sprite
    /// time; renderers read <see cref="SpriteFrame.Value"/> from snapshots.</summary>
    public Entity SpawnSpriteAnimation(Vector3 position, Quaternion rotation, float fps, int frameCount, bool loop) =>
        Current.CreateEntity(EntityBuilder.Create()
            .Add(new Position { Value = position })
            .Add(new Rotation { Value = rotation })
            .Add(new SpriteTime())
            .Add(new SpriteFrame())
            .Add(new SpriteConfig(fps, frameCount, loop))
            .Add(new SimulationContext { DeltaSeconds = (float)FixedDeltaSeconds }));

    /// <summary>Spawn a deterministic particle emitter; <paramref name="config"/> carries the
    /// authored config (build with the <see cref="ParticleConfig"/> constructor) and
    /// <paramref name="seed"/> seeds the paired runtime state's xorshift stream.</summary>
    public Entity SpawnParticleEmitter(Vector3 position, Quaternion rotation, in ParticleConfig config, uint seed) =>
        Current.CreateEntity(EntityBuilder.Create()
            .Add(new Position { Value = position })
            .Add(new Rotation { Value = rotation })
            .Add(config)
            .Add(ParticleConfig.SeedState(seed))
            .Add(new SimulationContext { DeltaSeconds = (float)FixedDeltaSeconds }));

    /// <summary>The optional sim-thread UI half. Set before <see cref="Start"/>; every tick the
    /// runner drains queued UI events into it and advances its time — so hover/focus/animations
    /// run in lockstep with game state. The renderer half of the same UI system runs on the
    /// render thread and synchronizes internally with this one.</summary>
    public IUiInput? UiInput { get; set; }

    /// <summary>Invoked ON THE SIM THREAD for pointer-downs the UI did not consume and that
    /// carry a world-space pick ray — the game-side "clicked the world" hook.</summary>
    public Action<WorldPointerEvent>? UiUnhandledPointerDown { get; set; }

    /// <summary>Queue a UI event from the platform/render thread; drained on the sim thread
    /// each tick, before movement input, so a click consumed by a UI panel never leaks into
    /// world interaction on the same tick.</summary>
    public void EnqueueUiEvent(in WorldPointerEvent uiEvent) => _uiEvents.Enqueue(uiEvent);

    /// <summary>Queue a plain window input, for a producer with no camera to project a ray with.</summary>
    public void EnqueueUiEvent(in WindowEvent input) => _uiEvents.Enqueue(new WorldPointerEvent(input));

    /// <summary>The optional sim-thread audio half (mirror of <see cref="UiInput"/>, data
    /// flowing the other way): game logic posts events/parameters through it on the sim
    /// thread and the runner advances its time each fixed tick. The system's pump half runs
    /// on the render thread.</summary>
    public IAudioSink? Audio { get; set; }

    /// <summary>Add a velocity delta to a dynamic ball on its next tick (the pool strike). When
    /// <paramref name="angularVelocity"/> is given it is ASSIGNED (not accumulated) as the ball's
    /// full 3D spin (english + draw/follow) — a fresh strike. Left null (the default), the impulse
    /// leaves spin untouched, so a non-strike velocity nudge never clobbers a spinning ball.</summary>
    public void EnqueueBallImpulse(Entity entity, Vector3 velocityDelta, Vector3? angularVelocity = null) =>
        _impulses.Enqueue((entity, velocityDelta, angularVelocity));

    /// <summary>Request a score reset (thread-safe). The sim thread emits a managed
    /// <see cref="GameReset"/> on its next tick (before the schedule commits); the
    /// <see cref="ScoreSystem"/> reactor zeroes <see cref="Score"/> the tick after that.</summary>
    public void RequestReset() => _resetRequested = true;

    /// <summary>The current pool score from the latest published snapshot (thread-safe). Written only by
    /// the <see cref="ScoreSystem"/> reactor, in response to <c>SystemEvents</c>.</summary>
    public int Score
    {
        get { lock (_lock) { return _live.Count == 0 ? 0 : _live[^1].World.GetComponent<Score>(_scoreEntity).Value; } }
    }

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
            write.GetComponent<Position>(ball.Entity).Value = ball.Position;
            write.GetComponent<Rotation>(ball.Entity).Value = ball.Rotation;
            write.GetComponent<Velocity>(ball.Entity).Value = ball.Velocity;
            write.GetComponent<AngularVelocity>(ball.Entity).Value = ball.AngularVelocity;
            write.GetComponent<BallGlow>(ball.Entity).Intensity = ball.Glow;
            // Restoring to a pre-sink frame resurrects the ball (positions are recorded every
            // tick, so the transform write above already moved it back onto the table).
            write.GetComponent<BallSunk>(ball.Entity).Value = ball.Sunk;
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

    // ---- Aim prediction (read-only forward sim of the current state) ----

    // Reused rollout scratch; access is serialized under _lock with TickOnce's publish.
    private DynamicSphere[] _predictSpheres = new DynamicSphere[8];
    private Entity[] _predictEntities = new Entity[8];
    private readonly List<RewoundBall> _predictRewind = new();

    /// <summary>
    /// Forward-simulate the CURRENT ball set with a tentative strike (<paramref name="aimImpulse"/>
    /// added to the cue's velocity, <paramref name="spinY"/> set as its english) and fill
    /// <paramref name="outPoints"/> with the cue ball's predicted world-space path — the aim
    /// preview. Runs the EXACT same stateless <see cref="RigidSphereDynamics"/> the sim ticks,
    /// so the trail matches reality: cushion bounces, the first object-ball contact, and english.
    /// Purely read-only — it copies component data out of the immutable latest snapshot and never
    /// mutates any published world; safe to call from the host render thread. When
    /// <paramref name="framesBack"/> &gt; 0 it seeds ball position/velocity/spin from that rewind
    /// frame (the scrubbed present) so a paused-and-scrubbed preview matches the staged strike
    /// applied on resume. Returns false when there is no cue or no snapshot yet.
    /// </summary>
    public bool PredictCueBallPath(Entity cue, Vector3 aimImpulse, Vector3 angularVelocity,
        List<Vector3> outPoints, int maxSteps, int framesBack = 0)
    {
        outPoints.Clear();
        // Fetch the optional rewind seed first (RewindBuffer has its own lock) so we never nest it
        // under _lock.
        bool useRewind = framesBack > 0 && _rewind.TryGet(framesBack, _predictRewind);

        lock (_lock)
        {
            if (_live.Count == 0) return false;
            World world = _live[^1].World;
            if (!world.IsAlive(cue) || !world.HasComponent<BallPhysicsConfig>(cue)) return false;

            int n = _ballEntities.Count;
            if (_predictSpheres.Length < n)
            {
                _predictSpheres = new DynamicSphere[n];
                _predictEntities = new Entity[n];
            }

            // Gather live (non-sunk) balls, mirroring MovementSystem.StepBalls.
            int live = 0, cueIndex = -1;
            for (int i = 0; i < n; i++)
            {
                Entity e = _ballEntities[i];
                if (!world.IsAlive(e) || !world.HasComponent<BallPhysicsConfig>(e)) continue;
                if (world.GetComponent<BallSunk>(e).Value != 0) continue;

                ref readonly BallPhysicsConfig cfg = ref world.GetComponent<BallPhysicsConfig>(e);
                Vector3 pos = world.GetComponent<Position>(e).Value;
                Vector3 vel = world.GetComponent<Velocity>(e).Value;
                Vector3 spin = world.GetComponent<AngularVelocity>(e).Value;
                if (useRewind)
                {
                    foreach (RewoundBall rb in _predictRewind)
                    {
                        if (rb.Entity == e) { pos = rb.Position; vel = rb.Velocity; spin = rb.AngularVelocity; break; }
                    }
                }
                _predictSpheres[live] = new DynamicSphere
                {
                    Position = pos, Velocity = vel, AngularVelocity = spin, Radius = cfg.Radius, Mass = cfg.Mass,
                    LinearDamping = cfg.LinearDamping, AngularDamping = cfg.AngularDamping,
                    Restitution = cfg.Restitution, Friction = cfg.Friction,
                };
                _predictEntities[live] = e;
                if (e == cue) cueIndex = live;
                live++;
            }
            if (cueIndex < 0) return false;

            // Apply the tentative strike to the cue copy.
            _predictSpheres[cueIndex].Velocity += aimImpulse;
            _predictSpheres[cueIndex].AngularVelocity = angularVelocity;

            // Batch-wide solver tuning from the first live ball — identical to StepBalls.
            Entity first = _predictEntities[0];
            PhysicsTuning tuning = world.GetComponent<PhysicsTuning>(first);
            var settings = SphereDynamicsSettings.Default with
            {
                StaticFilter = Physics.PhysicsLayers.BallContact,
                Gravity = tuning.Gravity,
                MinSpeed = tuning.MinSpeed,
                MinAngularSpeed = tuning.MinAngularSpeed,
                Skin = tuning.Skin,
                PushStrength = tuning.PushStrength,
                StaticFriction = tuning.StaticFriction,
                StaticRestitution = world.GetComponent<BallPhysicsConfig>(first).StaticRestitution,
            };
            CollisionWorldHandle statics = _collisionWorld?.Handle ?? default;
            PocketConfig cuePockets = world.GetComponent<PocketConfig>(cue);

            var span = new Span<DynamicSphere>(_predictSpheres, 0, live);
            var dt = (float)FixedDeltaSeconds;
            outPoints.Add(span[cueIndex].Position);
            for (int step = 0; step < maxSteps; step++)
            {
                RigidSphereDynamics.Step(span, ReadOnlySpan<KinematicCapsule>.Empty, statics, settings, dt);
                Vector3 p = span[cueIndex].Position;
                outPoints.Add(p);
                Vector3 v = span[cueIndex].Velocity;
                if (v.X * v.X + v.Z * v.Z < settings.MinSpeed * settings.MinSpeed) break; // came to rest
                if (InPocket(cuePockets, p)) break; // would drop
            }
            return outPoints.Count > 1;
        }
    }

    private static bool InPocket(in PocketConfig pool, Vector3 p)
    {
        // Pockets are packed as (centerX, centerZ, radius², unused) — the same planar XZ test
        // MovementSystem.CaptureInPocket uses.
        for (int i = 0; i < pool.PocketCount; i++)
        {
            Vector4 pocket = pool.Pockets[i];
            float dx = p.X - pocket.X, dz = p.Z - pocket.Y;
            if (dx * dx + dz * dz < pocket.Z) return true;
        }
        return false;
    }

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
            // The UI half sees the WINDOW event; the ray is the sample's own business. Copied to
            // a local first because Handle takes it by `in` and a property cannot be passed by ref.
            var input = uiEvent.Input;
            var consumed = ui?.Handle(in input) ?? false;
            if (!consumed && uiEvent is { IsPointerDown: true, HasWorldRay: true })
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

        while (_impulses.TryDequeue(out var impulse))
        {
            if (write.IsAlive(impulse.Entity) && write.HasComponent<Velocity>(impulse.Entity))
            {
                write.GetComponent<Velocity>(impulse.Entity).Value += impulse.VelocityDelta;
                if (impulse.Angular is { } w) write.GetComponent<AngularVelocity>(impulse.Entity).Value = w; // a strike sets spin; a plain nudge leaves it
            }
        }

        // MANAGED event producer: raise GameReset on the sim thread BEFORE the schedule commits, so
        // it publishes alongside the systems' appended events and is delivered to ScoreSystem next
        // tick (the deferred-bus contract). Mirrors immortal-cultivation's managed world.Events.Emit.
        if (_resetRequested)
        {
            _resetRequested = false;
            write.Events.Emit(new GameReset());
        }

        // MovementSystem (ball dynamics — the sole transform writer) runs inside the schedule.
        // Systems' read-only fields bind to `current` (the immutable previous-tick snapshot) —
        // snapshot-read mode.
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
        // Worldless since engine 0.19; the write world moves into the delegate below.
        var schedule = SystemSchedule.Create()
            .AddWorld<MovementSystem>()
            .AddWorld<SpriteAnimationSystem>()
            .AddWorld<ParticleSystem>()
            .AddWorld<ScoreSystem>()
            .Build(new SnapshotDagScheduler(), new ParallelWaveScheduler());
        SimulationTick.WarmSystemQueries(world);
        _schedules.Add(schedule);
        // NOT `schedule.Run` as a method group: that still compiles, binding to the one-argument
        // Run(world) overload, and the delegate is invoked with the READ twin — so the schedule
        // would step the read world and leave the write world untouched. Silent, and wrong. The
        // write world is captured explicitly instead.
        _runByWorld[world] = read => schedule.Run(world, read);
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
