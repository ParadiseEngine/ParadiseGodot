using Paradise.Sample.Odyssey;
using Paradise.Sample.Ui;

namespace Paradise.Sample.Ui.Tests;

/// <summary>Drives the MVVM <see cref="OdysseyViewModel"/> headlessly over a real single-threaded
/// <see cref="OdysseyRunner"/> — the same ViewModel the "Space Odyssey" View renders. Proves the
/// read-only projections track the sim (charging raises the warp-charge fraction; a charged warp
/// advances the sector or damages the hull through the event seam; the ship's log fills) and that
/// the managed-emit new-voyage command resets the ship.</summary>
public class OdysseyViewModelTests
{
    private static void ChargeToFull(OdysseyRunner runner)
    {
        runner.SetCharging(true);
        for (var i = 0; i < 5000 && runner.Energy < runner.EnergyToJump; i++)
        {
            runner.TickOnce();
        }
    }

    [Test]
    public async Task initial_projections_start_at_a_fresh_voyage()
    {
        using var runner = new OdysseyRunner();
        var vm = new OdysseyViewModel(runner);

        await Assert.That(vm.Sector).IsEqualTo(0);
        await Assert.That(vm.EnergyFraction).IsEqualTo(0.0);
        await Assert.That(vm.HullFraction).IsEqualTo(1.0);
        await Assert.That(vm.IsCharging).IsFalse();
        await Assert.That(vm.IsDestroyed).IsFalse();
        await Assert.That(vm.EnergyToJump).IsGreaterThan(0.0);
    }

    [Test]
    public async Task toggle_charging_raises_the_energy_fraction()
    {
        using var runner = new OdysseyRunner();
        var vm = new OdysseyViewModel(runner);

        vm.ToggleCharging();
        await Assert.That(vm.IsCharging).IsTrue();
        for (var i = 0; i < 30; i++) runner.TickOnce();

        await Assert.That(vm.EnergyFraction).IsGreaterThan(0.0);
        await Assert.That(vm.EnergyFraction).IsLessThanOrEqualTo(1.0);
    }

    [Test]
    public async Task a_charged_warp_advances_the_sector_or_damages_the_hull()
    {
        using var runner = new OdysseyRunner();
        var vm = new OdysseyViewModel(runner);

        ChargeToFull(runner);
        await Assert.That(vm.EnergyFraction).IsGreaterThanOrEqualTo(1.0);

        var hullBefore = vm.Hull;
        vm.Warp();
        for (var i = 0; i < 5; i++) runner.TickOnce(); // roll (tick N) → reactor applies (tick N+1)

        var advanced = vm.Sector == 1;
        var damaged = vm.Hull < hullBefore - 1.0;
        await Assert.That(advanced || damaged).IsTrue();
        // The jump drained the drive below full charge.
        await Assert.That(vm.EnergyFraction).IsLessThan(1.0);
        // The event seam wrote the ship's log.
        await Assert.That(vm.Log.Any(line => line.Contains("Warp jump"))).IsTrue();
    }

    [Test]
    public async Task new_voyage_resets_the_projections()
    {
        using var runner = new OdysseyRunner();
        var vm = new OdysseyViewModel(runner);

        // Advance a sector so there is state to reset.
        for (var attempt = 0; attempt < 12 && vm.Sector == 0 && !vm.IsDestroyed; attempt++)
        {
            ChargeToFull(runner);
            vm.Warp();
            for (var i = 0; i < 3; i++) runner.TickOnce();
        }

        vm.NewVoyage();                                 // managed bus emit
        for (var i = 0; i < 3; i++) runner.TickOnce();  // emit committed → reactor resets next tick

        await Assert.That(vm.Sector).IsEqualTo(0);
        await Assert.That(vm.HullFraction).IsGreaterThan(0.99);
        await Assert.That(vm.IsDestroyed).IsFalse();
    }
}
