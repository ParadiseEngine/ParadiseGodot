using System.Collections.Generic;
using Paradise.Sample.Odyssey;

namespace Paradise.Sample.Ui;

/// <summary>
/// The MVVM ViewModel for the "Space Odyssey" sample — a pure, host-agnostic projection over the
/// single-threaded snapshot sim (<see cref="OdysseyRunner"/>). It exposes READ-ONLY state (sector,
/// warp energy, hull, credits, distance, jump chance, ship's log) passed straight through the runner,
/// plus a couple of derived fractions the gauges bind to, and COMMAND methods (charge toggle, warp,
/// new voyage) that only forward to the runner. It has no ImGui dependency: the same ViewModel is what
/// Paradise.Sample.Ui.Tests drives headlessly.
///
/// Threading: every projection/command runs on the sim thread (the same thread that ticks the runner
/// and draws the View), so the reads are coherent with the tick that produced them.
/// </summary>
public sealed class OdysseyViewModel
{
    private readonly OdysseyRunner _runner;

    public OdysseyViewModel(OdysseyRunner runner) => _runner = runner;

    /// <summary>The current sector the voyage has reached.</summary>
    public int Sector => _runner.Sector;

    /// <summary>Accumulated warp-drive energy.</summary>
    public double Energy => _runner.Energy;

    /// <summary>Energy required for one warp jump.</summary>
    public double EnergyToJump => _runner.EnergyToJump;

    /// <summary>Charge fraction [0..1+] for the warp-charge gauge.</summary>
    public double EnergyFraction => EnergyToJump > 0 ? Energy / EnergyToJump : 0.0;

    /// <summary>Current hull integrity.</summary>
    public double Hull => _runner.Hull;

    /// <summary>Hull integrity at full.</summary>
    public double FullHull => _runner.FullHull;

    /// <summary>Hull fraction [0..1] for the hull gauge.</summary>
    public double HullFraction => FullHull > 0 ? Hull / FullHull : 0.0;

    /// <summary>Credits earned across the voyage.</summary>
    public int CreditBalance => _runner.CreditBalance;

    /// <summary>Distance travelled, light-years.</summary>
    public double Distance => _runner.Distance;

    /// <summary>Whether the warp drive is currently charging.</summary>
    public bool IsCharging => _runner.IsCharging;

    /// <summary>Whether the hull has breached (voyage lost until New Voyage).</summary>
    public bool IsDestroyed => _runner.IsDestroyed;

    /// <summary>Success chance of the next jump (sector-scaled), [0..1].</summary>
    public float JumpChance => _runner.JumpChance;

    /// <summary>The ship's log (oldest → newest, bounded).</summary>
    public IReadOnlyList<string> Log => _runner.Log;

    /// <summary>Toggle the warp drive charging on/off.</summary>
    public void ToggleCharging() => _runner.SetCharging(!_runner.IsCharging);

    /// <summary>Request a warp jump (a no-op unless charged and intact — the runner gates it).</summary>
    public void Warp() => _runner.RequestWarp();

    /// <summary>Begin a fresh voyage (managed bus emit; the reactors reset next tick).</summary>
    public void NewVoyage() => _runner.RequestNewVoyage();
}
