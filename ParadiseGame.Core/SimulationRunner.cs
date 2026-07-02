using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using ParadiseGame.Core.Navigation;
using ParadiseGame.Core.Physics;

namespace ParadiseGame.Core;

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
    private readonly ConcurrentDictionary<Entity, Vector3> _moveInput = new();
    private readonly object _lock = new();
    private readonly Stopwatch _clock = new();

    // All under _lock (except where noted). _live is oldest→newest; last is the latest ("current").
    private readonly List<Snapshot> _live = new();
    private readonly Stack<World> _pool = new();
    private readonly List<IDisposable> _schedules = new();
    private readonly Dictionary<World, Action> _runByWorld = new();
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

    public Entity SpawnAgent(Vector3 position, Quaternion rotation, float moveSpeed, float angularSpeed, float arriveRadius,
        float bodyRadius = 0.4f, float bodyHalfLength = 0.5f) =>
        Current.CreateEntity(EntityBuilder.Create()
            .Add(new LocalTransform(position, rotation))
            .Add(new NavAgent(moveSpeed, angularSpeed, arriveRadius))
            .Add(new NavPath())
            .Add(new MoveIntent())
            .Add(new CharacterBody(bodyRadius, bodyHalfLength))
            .Add(new SimulationContext()));

    /// <summary>Spawn a dynamic physics ball (sphere). Position is the sphere center.</summary>
    public Entity SpawnBall(Vector3 position, Quaternion rotation, float radius, float mass = 1f) =>
        Current.CreateEntity(EntityBuilder.Create()
            .Add(new LocalTransform(position, rotation))
            .Add(new DynamicBody(radius, mass)));

    public void EnqueueMoveTo(Entity entity, Vector3 target) => _input.Enqueue(new MoveCommand(entity, target));

    /// <summary>Set an agent's current direct-move (WASD) direction; applied every tick until changed
    /// (zero = no input). Overrides click-to-move path following while non-zero.</summary>
    public void SetMoveInput(Entity entity, Vector3 direction) => _moveInput[entity] = direction;

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
                    TickOnce();
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

    // ---- One double-buffered frame (also drives the headless tests synchronously) ----

    public void TickOnce()
    {
        World current;
        World write;
        lock (_lock)
        {
            if (_pool.Count == 0)
            {
                // Every world is pinned — a stalled renderer is still reading them all. Skip this tick
                // (backpressure); the sim resumes once the renderer releases a pin and prune refills the pool.
                return;
            }
            current = _live[^1].World; // read the current snapshot ref under the lock
            write = _pool.Pop();
        }

        // Rules 2 + 3: read current (read-only, immutable), write the new world — outside the lock.
        write.CopyFrom(current);

        SimulationTick.PrepareFrame(write, (float)FixedDeltaSeconds);

        while (_input.TryDequeue(out MoveCommand cmd))
        {
            if (write.IsAlive(cmd.Entity))
            {
                NavigationPlanner.PlanMoveTo(write, cmd.Entity, cmd.Target, _navigationMesh);
            }
        }

        // Steering: path following writes MoveIntent (no position writes).
        _runByWorld[write]();

        // Direct (WASD) input — applied after the schedule so it overrides the path intent this
        // tick and clears the path for subsequent ones.
        foreach (var kv in _moveInput)
        {
            if (write.IsAlive(kv.Key))
            {
                DirectMover.Apply(write, kv.Key, kv.Value);
            }
        }

        // Integration: resolve intents against the static collision world (planar, Y untouched),
        // then run the dynamics step (character pushes, ball↔static, ball↔ball).
        CharacterMoveIntegrator.Step(write, _collisionWorld, (float)FixedDeltaSeconds);
        DynamicBodyIntegrator.Step(write, _collisionWorld, (float)FixedDeltaSeconds);

        lock (_lock)
        {
            _live.Add(new Snapshot { World = write, Frame = ++_frame });
            PruneUnlocked();
        }
    }

    // Recycle snapshots that are older than the interpolation window (keep the 2 newest) AND not pinned by
    // the renderer. Pinned snapshots are kept until released, so a world is never reused mid-read.
    private void PruneUnlocked()
    {
        while (_live.Count > 2 && _live[0].Pinned == 0)
        {
            _pool.Push(_live[0].World);
            _live.RemoveAt(0);
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
    private World CreateWorldWithSchedule()
    {
        World world = _shared.CreateWorld();
        var schedule = SystemSchedule.Create(world).Add<NavMeshFollowSystem>().Build<SequentialWaveScheduler>();
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
