using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace Paradise.Sample.Odyssey;

/// <summary>
/// Drives the "Space Odyssey" sim as a sequence of IMMUTABLE ECS-world snapshots on its own sim thread —
/// the same threaded double-buffer model as Paradise.Sample.Pool's <c>SimulationRunner</c>, so both 3D
/// hosts (SDL + Godot) can read entity transforms while the sim keeps ticking. Each tick rents a fresh
/// write-world, <c>CopyFrom</c>s the current (immutable) world as the read source, writes the new world,
/// and publishes it; a published world is never mutated again. The render thread pins the latest two
/// published worlds via <see cref="TrySampleInterpolation"/> and interpolates.
///
/// One ship entity (all the abstract warp/hull components PLUS the spatial ones) and a FIXED roster of
/// body entities (a star, planets, asteroids, a warp gate) live on the initial snapshot; <c>CopyFrom</c>
/// carries them into every later tick. The set never changes — a warp RESHUFFLES the bodies' orbit
/// config (managed, between ticks) rather than respawning them, so hosts build their instances once.
///
/// Gameplay: charge the drive (<see cref="SetCharging"/>) and pilot the ship (<see cref="SetThrust"/> /
/// <see cref="SetTurn"/>) into the warp gate — when charged and inside the gate's capture radius the
/// runner raises the <see cref="WarpIntent"/> (the fly-to-gate trigger; <see cref="RequestWarp"/> is a
/// manual equivalent for the HUD button / tests), WarpSystem rolls it, and on success VoyageSystem
/// advances the sector while the runner regenerates the map. <see cref="TickOnce"/> is public so the
/// tests drive it synchronously without <see cref="Start"/>.
/// </summary>
public sealed class OdysseyRunner : IDisposable
{
    public const double FixedDeltaSeconds = 1.0 / 60.0;
    private const double MaxAccumulatedSeconds = 0.25;
    // Worlds are pre-created on the owner thread (SharedWorld.CreateWorld is the only affinity-guarded
    // op). Large enough to absorb render-thread stalls that pin snapshots and block recycling.
    private const int PoolSize = 32;

    private sealed class Snapshot
    {
        public required World World;
        public long Frame;
        public int Pinned;
        public double Time => Frame * FixedDeltaSeconds;
    }

    /// <summary>A render descriptor read ONCE at spawn (fixed per entity across warps): what mesh/scale/
    /// colour a host should build for this body. Per-frame transforms are read off the sampled snapshot.</summary>
    public readonly record struct RenderBody(Entity Entity, int Kind, float Scale, Vector4 Tint);

    private readonly SharedWorld _shared;
    private readonly object _lock = new();
    private readonly object _cmdLock = new();
    private readonly object _logLock = new();
    private readonly Stopwatch _clock = new();

    // All under _lock. _live is oldest→newest; last is the "current".
    private readonly List<Snapshot> _live = new();
    private readonly Stack<World> _pool = new();
    private readonly List<IDisposable> _schedules = new();
    private readonly Dictionary<World, Action<World>> _runByWorld = new();
    private long _heldFrameA = -1;
    private long _heldFrameB = -1;

    private readonly uint _seed;
    private Entity _ship;
    private Entity _gate;
    private readonly List<RenderBody> _bodies = new();
    private readonly List<string> _log = new();
    private bool _wasInGate;

    // Command latches (owner/UI thread writes, sim thread reads) — guarded by _cmdLock.
    private float _thrustCmd;
    private float _turnCmd;
    private bool _chargingCmd;
    private bool _warpRequested;
    private bool _newVoyageRequested;

    private long _frame;
    private volatile bool _running;
    private Thread? _thread;
    private volatile Exception? _threadException;
    private bool _disposed;

    public OdysseyRunner(uint seed = 0x0D155EE5u)
    {
        _seed = seed == 0 ? 1u : seed;
        _shared = SharedWorldFactory.Create();
        for (int i = 0; i < PoolSize; i++)
        {
            _pool.Push(CreateWorldWithSchedule());
        }
        _live.Add(new Snapshot { World = RentWorldUnlocked(), Frame = 0 });

        SpawnVoyage(Current);
        RegenerateSector(Current, 0); // lay out the first sector's map + place the ship at its entry

        _log.Add("Voyage begins - Sector 0.");
    }

    private World Current => _live[^1].World; // sim-thread only (sole writer of _live)

    /// <summary>The authored warp/hull tuning (the config bag baked onto the ship).</summary>
    public static SectorLadder DefaultLadder => new()
    {
        EnergyPerJump = 100.0,
        ChargeRate = 45.0,
        CruiseSpeed = 2.0,
        BaseJumpChance = 0.85f,
        ChancePenaltyPerSector = 0.06f,
        MinJumpChance = 0.25f,
        HullDrainPerSec = 1.5,
        HullDamageOnFail = 22.0,
        HullRepairOnJump = 8.0,
        FullHull = 100.0,
        CreditsPerJump = 25,
    };

    /// <summary>The authored flight tuning (the config bag baked onto the ship).</summary>
    public static FlightConfig DefaultFlight => new()
    {
        ThrustAccel = 26f,
        TurnRate = 2.2f,
        LinearDamping = 0.9f,
        MaxSpeed = 22f,
        SectorBounds = 45f,
        GateCaptureRadius = 4.5f,
    };

    // ---- spawning (owner thread, before Start) ----

    private void SpawnVoyage(World world)
    {
        SectorLadder ladder = DefaultLadder;
        FlightConfig flight = DefaultFlight;

        _ship = world.CreateEntity(EntityBuilder.Create()
            // abstract warp/hull state
            .Add(new SimulationContext { DeltaSeconds = (float)FixedDeltaSeconds })
            .Add(new SectorIndex { Value = 0 })
            .Add(new WarpEnergy { Value = 0.0 })
            .Add(new DistanceLy { Value = 0.0 })
            .Add(new HullIntegrity { Value = ladder.FullHull })
            .Add(new Credits { Value = 0 })
            .Add(new Charging { Value = 0 })
            .Add(new WarpIntent { Pending = 0 })
            .Add(new RngState { Value = _seed })
            .Add(new Destroyed { Value = 0 })
            .Add(ladder)
            // spatial / piloting state
            .Add(new Position { Value = Vector3.Zero })
            .Add(new Rotation { Value = Quaternion.Identity })
            .Add(new Velocity { Value = Vector3.Zero })
            .Add(new Heading { Value = 0f })
            .Add(new ThrustInput { Value = 0f })
            .Add(new TurnInput { Value = 0f })
            .Add(flight));

        // Fixed body roster. Kind/scale/tint are fixed per entity (so a host bakes a material once);
        // orbit config is (re)assigned by RegenerateSector each sector.
        SpawnBody(world, kind: 0, scale: 3.0f, tint: new Vector4(1.0f, 0.85f, 0.45f, 1f), spinSpeed: 0.15f); // star
        var planetTints = new[]
        {
            new Vector4(0.45f, 0.62f, 0.95f, 1f),
            new Vector4(0.80f, 0.45f, 0.35f, 1f),
            new Vector4(0.55f, 0.80f, 0.60f, 1f),
            new Vector4(0.70f, 0.60f, 0.85f, 1f),
        };
        var planetScales = new[] { 1.4f, 0.9f, 1.1f, 0.75f };
        for (int p = 0; p < planetTints.Length; p++)
        {
            SpawnBody(world, kind: 1, scale: planetScales[p], tint: planetTints[p], spinSpeed: 0.4f);
        }
        for (int a = 0; a < 6; a++)
        {
            SpawnBody(world, kind: 2, scale: 0.30f, tint: new Vector4(0.55f, 0.55f, 0.58f, 1f), spinSpeed: 0.8f);
        }
        _gate = SpawnBody(world, kind: 3, scale: 2.2f, tint: new Vector4(0.35f, 0.95f, 1.0f, 1f), spinSpeed: 0.9f);
    }

    private Entity SpawnBody(World world, int kind, float scale, Vector4 tint, float spinSpeed)
    {
        Entity e = world.CreateEntity(EntityBuilder.Create()
            .Add(new SimulationContext { DeltaSeconds = (float)FixedDeltaSeconds })
            .Add(new Position { Value = Vector3.Zero })
            .Add(new Rotation { Value = Quaternion.Identity })
            .Add(new BodyKind { Value = kind })
            .Add(new BodyScale { Value = scale })
            .Add(new BodyTint { Value = tint })
            .Add(new OrbitCenter { Value = Vector3.Zero })
            .Add(new OrbitRadius { Value = 0f })
            .Add(new OrbitAngle { Value = 0f })
            .Add(new OrbitSpeed { Value = 0f })
            .Add(new SpinPhase { Value = 0f })
            .Add(new SpinSpeed { Value = spinSpeed }));
        _bodies.Add(new RenderBody(e, kind, scale, tint));
        return e;
    }

    /// <summary>Reshuffle the sector map (planets/asteroids to fresh orbits, the gate to a new spot)
    /// deterministically from (seed, sector) and drop the ship at the sector's entry point facing the
    /// gate. All managed, untracked writes — run between ticks on the write world.</summary>
    private void RegenerateSector(World world, int sector)
    {
        uint rng = Hash(_seed, unchecked((uint)sector));
        FlightConfig flight = world.GetComponent<FlightConfig>(_ship);
        const float Tau = MathF.PI * 2f;

        // Gate: a fresh anchor out toward the sector edge, at a random bearing + a little height.
        float gateBearing = NextFloat(ref rng) * Tau;
        float gateDist = flight.SectorBounds * 0.62f;
        float gateY = (NextFloat(ref rng) - 0.5f) * 8f;
        var gatePos = new Vector3(MathF.Cos(gateBearing) * gateDist, gateY, MathF.Sin(gateBearing) * gateDist);

        for (int i = 0; i < _bodies.Count; i++)
        {
            Entity e = _bodies[i].Entity;
            int kind = _bodies[i].Kind;
            switch (kind)
            {
                case 0: // star — parked at the origin, no orbit
                    world.GetComponent<OrbitCenter>(e).Value = Vector3.Zero;
                    world.GetComponent<OrbitRadius>(e).Value = 0f;
                    world.GetComponent<OrbitSpeed>(e).Value = 0f;
                    break;
                case 3: // gate — parked at its new anchor
                    world.GetComponent<OrbitCenter>(e).Value = gatePos;
                    world.GetComponent<OrbitRadius>(e).Value = 0f;
                    world.GetComponent<OrbitSpeed>(e).Value = 0f;
                    break;
                case 1: // planet — a wide, slow orbit about the star
                {
                    world.GetComponent<OrbitCenter>(e).Value = Vector3.Zero;
                    world.GetComponent<OrbitRadius>(e).Value = 8f + i * 5f + NextFloat(ref rng) * 3f;
                    world.GetComponent<OrbitAngle>(e).Value = NextFloat(ref rng) * Tau;
                    float dir = NextFloat(ref rng) < 0.5f ? -1f : 1f;
                    world.GetComponent<OrbitSpeed>(e).Value = (0.10f + NextFloat(ref rng) * 0.30f) * dir;
                    break;
                }
                default: // asteroid — a tighter, faster scatter
                {
                    world.GetComponent<OrbitCenter>(e).Value = Vector3.Zero;
                    world.GetComponent<OrbitRadius>(e).Value = 5f + NextFloat(ref rng) * 26f;
                    world.GetComponent<OrbitAngle>(e).Value = NextFloat(ref rng) * Tau;
                    float dir = NextFloat(ref rng) < 0.5f ? -1f : 1f;
                    world.GetComponent<OrbitSpeed>(e).Value = (0.20f + NextFloat(ref rng) * 0.60f) * dir;
                    break;
                }
            }
        }

        // Drop the ship out in open space at a bearing 90° off the gate (so the star sits to one side,
        // not dead ahead) facing the gate — a clear diagonal run past the star to fly.
        float entryBearing = gateBearing + MathF.PI * 0.5f;
        float entryDist = flight.SectorBounds * 0.45f;
        var entry = new Vector3(MathF.Cos(entryBearing) * entryDist, 0f, MathF.Sin(entryBearing) * entryDist);
        world.GetComponent<Position>(_ship).Value = entry;
        world.GetComponent<Velocity>(_ship).Value = Vector3.Zero;
        world.GetComponent<Heading>(_ship).Value = MathF.Atan2(gatePos.X - entry.X, gatePos.Z - entry.Z);
        _wasInGate = false;
    }

    // ---- commands (any thread) ----

    /// <summary>Set the pilot forward thrust in [-1..1] (latched; applied to the ship each tick).</summary>
    public void SetThrust(float thrust)
    {
        lock (_cmdLock) { _thrustCmd = Math.Clamp(thrust, -1f, 1f); }
    }

    /// <summary>Set the pilot yaw turn in [-1..1] (latched; +yaw).</summary>
    public void SetTurn(float turn)
    {
        lock (_cmdLock) { _turnCmd = Math.Clamp(turn, -1f, 1f); }
    }

    /// <summary>Toggle the warp drive charging.</summary>
    public void SetCharging(bool on)
    {
        lock (_cmdLock) { _chargingCmd = on; }
    }

    /// <summary>Manually request a warp (the HUD button / tests) — the runner raises the intent on its
    /// next tick if the drive is charged and the hull is intact (fly-to-gate does the same automatically).</summary>
    public void RequestWarp()
    {
        lock (_cmdLock) { _warpRequested = true; }
    }

    /// <summary>Begin a fresh voyage — a MANAGED bus emit on the sim thread; the owner-reactors reset
    /// their state next tick and the runner regenerates sector 0.</summary>
    public void RequestNewVoyage()
    {
        lock (_cmdLock) { _newVoyageRequested = true; }
    }

    // ---- render enumeration (any thread; the sets are fixed after construction) ----

    /// <summary>The ship entity — read its <see cref="Position"/>/<see cref="Rotation"/> off a sampled
    /// snapshot to place the ship mesh.</summary>
    public Entity Ship => _ship;

    /// <summary>The fixed body roster (mesh kind + scale + tint per entity). Per-frame transforms come
    /// from the sampled snapshot.</summary>
    public IReadOnlyList<RenderBody> Bodies => _bodies;

    // ---- state accessors (thread-safe locked snapshot reads) ----

    private T Read<T>(Func<World, T> read)
    {
        lock (_lock) { return _live.Count == 0 ? default! : read(_live[^1].World); }
    }

    public int Sector => Read(w => w.GetComponent<SectorIndex>(_ship).Value);
    public double Energy => Read(w => w.GetComponent<WarpEnergy>(_ship).Value);
    public double EnergyToJump => Read(w => w.GetComponent<SectorLadder>(_ship).EnergyPerJump);
    public double Hull => Read(w => w.GetComponent<HullIntegrity>(_ship).Value);
    public double FullHull => Read(w => w.GetComponent<SectorLadder>(_ship).FullHull);
    public int CreditBalance => Read(w => w.GetComponent<Credits>(_ship).Value);
    public double Distance => Read(w => w.GetComponent<DistanceLy>(_ship).Value);
    /// <summary>Whether the drive is commanded to charge — the latched command (immediate), not the
    /// one-tick-later snapshot, so the HUD's "Charging…" toggle reflects the press right away.</summary>
    public bool IsCharging { get { lock (_cmdLock) { return _chargingCmd; } } }
    public bool IsDestroyed => Read(w => w.GetComponent<Destroyed>(_ship).Value != 0);

    /// <summary>The current jump success chance (sector-scaled), for the HUD readout.</summary>
    public float JumpChance => Read(w =>
    {
        SectorLadder c = w.GetComponent<SectorLadder>(_ship);
        return Math.Max(c.MinJumpChance, c.BaseJumpChance - c.ChancePenaltyPerSector * w.GetComponent<SectorIndex>(_ship).Value);
    });

    /// <summary>Distance from the ship to the warp gate (units) — HUD guidance to "fly to the gate".</summary>
    public float GateDistance => Read(w =>
        Vector3.Distance(w.GetComponent<Position>(_ship).Value, w.GetComponent<OrbitCenter>(_gate).Value));

    /// <summary>The ship's current world position (thread-safe snapshot read).</summary>
    public Vector3 ShipPosition => Read(w => w.GetComponent<Position>(_ship).Value);

    /// <summary>The ship's current velocity (thread-safe snapshot read).</summary>
    public Vector3 ShipVelocity => Read(w => w.GetComponent<Velocity>(_ship).Value);

    /// <summary>The ship's log (oldest→newest, bounded) — a thread-safe copy.</summary>
    public IReadOnlyList<string> Log
    {
        get { lock (_logLock) { return _log.ToArray(); } }
    }

    // ---- threading ----

    public double Now => _clock.Elapsed.TotalSeconds;
    public bool HasSnapshots { get { lock (_lock) { return _live.Count > 0; } } }
    public double LatestSnapshotTime { get { lock (_lock) { return _live.Count == 0 ? 0 : _live[^1].Time; } } }
    public Exception? ThreadException => _threadException;

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("Already started.");
        _running = true;
        _clock.Start();
        _thread = new Thread(Run) { IsBackground = true, Name = "OdysseySim" };
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

    // ---- one double-buffered frame (also drives the tests synchronously) ----

    public void TickOnce()
    {
        // Snapshot the command latches once for this tick.
        float thrust; float turn; bool charging; bool manualWarp; bool newVoyage;
        lock (_cmdLock)
        {
            thrust = _thrustCmd; turn = _turnCmd; charging = _chargingCmd;
            manualWarp = _warpRequested; _warpRequested = false;
            newVoyage = _newVoyageRequested; _newVoyageRequested = false;
        }

        World current;
        World write;
        lock (_lock)
        {
            if (_pool.Count == 0)
            {
                PruneUnlocked(); // empty pool must prune here (publish is what normally prunes)
            }
            if (_pool.Count == 0)
            {
                return; // every world pinned by a stalled renderer — backpressure, retry next tick
            }
            current = _live[^1].World;
            write = _pool.Pop();
        }

        write.CopyFrom(current);
        SimulationTick.PrepareFrame(write, (float)FixedDeltaSeconds);

        // Managed pre-pass: apply pilot commands + raise the warp intent BEFORE the schedule so
        // WarpSystem (writable WarpIntent binds to the write world) rolls it this tick.
        write.GetComponent<ThrustInput>(_ship).Value = thrust;
        write.GetComponent<TurnInput>(_ship).Value = turn;
        write.GetComponent<Charging>(_ship).Value = (byte)(charging ? 1 : 0);

        bool destroyedBefore = write.GetComponent<Destroyed>(_ship).Value != 0;

        if (newVoyage)
        {
            write.Events.Emit(new NewVoyage()); // reactors reset sector/hull/energy next tick
        }

        // Warp trigger: manual (button/tests) OR fly-to-gate on the rising edge of "charged & inside
        // the gate". Positions read here are last tick's (MotionSystem hasn't run yet) — a negligible lag.
        bool charged = write.GetComponent<WarpEnergy>(_ship).Value
                       >= write.GetComponent<SectorLadder>(_ship).EnergyPerJump;
        bool destroyed = destroyedBefore;
        Vector3 shipPos = write.GetComponent<Position>(_ship).Value;
        Vector3 gatePos = write.GetComponent<OrbitCenter>(_gate).Value;
        float capture = write.GetComponent<FlightConfig>(_ship).GateCaptureRadius;
        bool inGate = charged && !destroyed
                      && Vector3.DistanceSquared(shipPos, gatePos) <= capture * capture;
        if ((manualWarp && charged && !destroyed) || (inGate && !_wasInGate))
        {
            write.GetComponent<WarpIntent>(_ship).Pending = 1;
        }
        _wasInGate = inGate;

        int sectorBefore = write.GetComponent<SectorIndex>(_ship).Value;

        // Schedule: MotionSystem + ChargeSystem + WarpSystem + VoyageSystem, one snapshot-read wave.
        _runByWorld[write](current);

        int sectorAfter = write.GetComponent<SectorIndex>(_ship).Value;
        if (sectorAfter != sectorBefore)
        {
            RegenerateSector(write, sectorAfter); // warp landed (or a new-voyage 3→0 reset): fresh map
        }
        else if (newVoyage)
        {
            RegenerateSector(write, 0); // new voyage already at sector 0 — still relay to a fresh map
        }

        DrainLog(write, destroyedBefore);

        lock (_lock)
        {
            _live.Add(new Snapshot { World = write, Frame = ++_frame });
            PruneUnlocked();
        }
    }

    private void DrainLog(World world, bool destroyedBefore)
    {
        lock (_logLock)
        {
            foreach (var w in world.Events.Incoming<WarpResolved>())
            {
                Append(w.Succeeded != 0
                    ? $"Warp jump -> Sector {w.NewSector}: SUCCESS."
                    : $"Warp jump failed — hull damage {(-w.HullDelta):F0}.");
            }
            if (world.Events.Incoming<NewVoyage>().Length > 0)
            {
                Append("New voyage - drive reset, hull restored.");
            }
            if (world.GetComponent<Destroyed>(_ship).Value != 0 && !destroyedBefore)
            {
                Append("HULL BREACH - voyage lost.");
            }
        }
    }

    private void Append(string line) // caller holds _logLock
    {
        _log.Add(line);
        if (_log.Count > 64)
        {
            _log.RemoveAt(0);
        }
    }

    // ---- pool plumbing (mirrors SimulationRunner) ----

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

    private World RentWorldUnlocked()
    {
        if (_pool.Count == 0)
        {
            throw new InvalidOperationException(
                $"World pool exhausted ({PoolSize}) — the render thread stalled too long while holding snapshots.");
        }
        return _pool.Pop();
    }

    private World CreateWorldWithSchedule()
    {
        World world = _shared.CreateWorld();
        // Worldless since engine 0.19: the schedule is a program over systems, and every Run
        // names both worlds. The write world it used to be bound to moves into the delegate below.
        var schedule = SystemSchedule.Create()
            .AddWorld<MotionSystem>()
            .AddWorld<ChargeSystem>()
            .AddWorld<WarpSystem>()
            .AddWorld<VoyageSystem>()
            .Build(new SnapshotDagScheduler(), new ParallelWaveScheduler());
        SimulationTick.WarmSystemQueries(world);
        _schedules.Add(schedule);
        // Captures the WRITE world this schedule was made for, so the stored delegate keeps its
        // shape: call it with the read twin and it steps that world.
        _runByWorld[world] = read => schedule.Run(world, read);
        return world;
    }

    // ---- snapshot sampling for interpolation (single reader) ----

    /// <summary>Pin and return the two published snapshots bracketing <paramref name="sampleTime"/>
    /// plus the interpolation factor. The pair stays pinned until the next call releases it. Out of
    /// range clamps to one snapshot (alpha 0). False only if no snapshot exists yet.</summary>
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

    // xorshift32 — the repo's determinism pattern; per-sector managed reshuffle stream (never seeded 0).
    private static uint Hash(uint seed, uint sector)
    {
        uint h = seed ^ (sector * 0x9E3779B9u);
        if (h == 0) h = 1u;
        h ^= h << 13; h ^= h >> 17; h ^= h << 5;
        return h == 0 ? 1u : h;
    }

    private static uint NextUInt(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static float NextFloat(ref uint state) => (NextUInt(ref state) >> 8) * (1f / 16777216f);

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
