using System;
using System.Collections.Generic;

namespace Paradise.Sample.Odyssey;

/// <summary>
/// Drives the "Space Odyssey" sim — the single-threaded snapshot runner (the <c>GameSimulation</c>
/// analog): one ship entity, a three-system schedule (<see cref="ChargeSystem"/>, <see cref="WarpSystem"/>,
/// <see cref="VoyageSystem"/>) run in one snapshot-read parallel wave each <see cref="TickOnce"/>.
/// Commands write intents/flags (managed) or <c>Emit</c> a bus event; state accessors read the ship's
/// components; and after each tick the ship's log is fed from the frame's <see cref="WarpResolved"/>
/// events. Meant to be ticked + read on ONE thread (the ImGui sample's sim thread).
/// </summary>
public sealed class OdysseyRunner : IDisposable
{
    public const double FixedDeltaSeconds = 1.0 / 60.0;

    private readonly SharedWorld _shared;
    private readonly World _world;
    private readonly World _previous;
    private readonly IDisposable _schedule;
    private readonly Action<World> _run;
    private readonly Entity _ship;
    private readonly List<string> _log = new();
    private bool _disposed;

    public OdysseyRunner(uint seed = 0x0D155EE5u)
    {
        _shared = SharedWorldFactory.Create();
        _world = _shared.CreateWorld();
        _previous = _shared.CreateWorld();

        SectorLadder cfg = DefaultLadder;
        _ship = _world.CreateEntity(EntityBuilder.Create()
            .Add(new SimulationContext { DeltaSeconds = (float)FixedDeltaSeconds })
            .Add(new SectorIndex { Value = 0 })
            .Add(new WarpEnergy { Value = 0.0 })
            .Add(new DistanceLy { Value = 0.0 })
            .Add(new HullIntegrity { Value = cfg.FullHull })
            .Add(new Credits { Value = 0 })
            .Add(new Charging { Value = 0 })
            .Add(new WarpIntent { Pending = 0 })
            .Add(new RngState { Value = seed == 0 ? 1u : seed }) // xorshift must never be seeded 0
            .Add(new Destroyed { Value = 0 })
            .Add(cfg));

        var schedule = SystemSchedule.Create(_world)
            .AddWorld<ChargeSystem>()
            .AddWorld<WarpSystem>()
            .AddWorld<VoyageSystem>()
            .Build(new SnapshotDagScheduler(), new ParallelWaveScheduler());
        SimulationTick.WarmSystemQueries(_world);
        _schedule = schedule;
        _run = schedule.Run;

        _log.Add("Voyage begins — Sector 0.");
    }

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

    // ---- commands (call on the sim thread) ----

    /// <summary>Toggle the warp drive charging (the "cultivate" action).</summary>
    public void SetCharging(bool on) => _world.GetComponent<Charging>(_ship).Value = (byte)(on ? 1 : 0);

    /// <summary>Request a warp jump — writes the intent WarpSystem rolls next tick. No-op unless the
    /// drive is charged and the hull is intact (the UI gates on the same conditions).</summary>
    public void RequestWarp()
    {
        if (!IsDestroyed && Energy >= EnergyToJump)
        {
            _world.GetComponent<WarpIntent>(_ship).Pending = 1;
        }
    }

    /// <summary>Begin a fresh voyage — a MANAGED bus emit (engine 0.5.2); the owner-reactors reset
    /// their state next tick.</summary>
    public void RequestNewVoyage() => _world.Events.Emit(new NewVoyage());

    // ---- state (read on the sim thread) ----

    public int Sector => _world.GetComponent<SectorIndex>(_ship).Value;
    public double Energy => _world.GetComponent<WarpEnergy>(_ship).Value;
    public double EnergyToJump => _world.GetComponent<SectorLadder>(_ship).EnergyPerJump;
    public double Hull => _world.GetComponent<HullIntegrity>(_ship).Value;
    public double FullHull => _world.GetComponent<SectorLadder>(_ship).FullHull;
    public int CreditBalance => _world.GetComponent<Credits>(_ship).Value;
    public double Distance => _world.GetComponent<DistanceLy>(_ship).Value;
    public bool IsCharging => _world.GetComponent<Charging>(_ship).Value != 0;
    public bool IsDestroyed => _world.GetComponent<Destroyed>(_ship).Value != 0;

    /// <summary>The current jump success chance (sector-scaled), for the UI readout.</summary>
    public float JumpChance
    {
        get
        {
            SectorLadder c = _world.GetComponent<SectorLadder>(_ship);
            return Math.Max(c.MinJumpChance, c.BaseJumpChance - c.ChancePenaltyPerSector * Sector);
        }
    }

    /// <summary>The ship's log (oldest→newest, bounded) — themed strings drained from the bus.</summary>
    public IReadOnlyList<string> Log => _log;

    public void TickOnce()
    {
        bool destroyedBefore = IsDestroyed;

        _previous.CopyFrom(_world);
        SimulationTick.PrepareFrame(_world, (float)FixedDeltaSeconds);
        _run(_previous);

        // Ship's log: WarpSystem's events committed by this tick's schedule are readable now.
        foreach (var w in _world.Events.Incoming<WarpResolved>())
        {
            Append(w.Succeeded != 0
                ? $"Warp jump → Sector {w.NewSector}: SUCCESS."
                : $"Warp jump failed — hull damage {(-w.HullDelta):F0}.");
        }
        if (_world.Events.Incoming<NewVoyage>().Length > 0)
        {
            Append("New voyage — drive reset, hull restored.");
        }
        if (IsDestroyed && !destroyedBefore)
        {
            Append("HULL BREACH — voyage lost.");
        }
    }

    private void Append(string line)
    {
        _log.Add(line);
        if (_log.Count > 64)
        {
            _log.RemoveAt(0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _schedule.Dispose();
        _shared.Dispose();
    }
}
