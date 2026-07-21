namespace Paradise.Sample.Odyssey.Tests;

/// <summary>The "Space Odyssey" sim: charging accumulates warp energy; a charged jump resolves through
/// the SystemEvents bus into an advanced sector or hull damage; the managed-emit new-voyage resets the
/// ship; and an undriven ship eventually breaches its hull. Driven synchronously via TickOnce.</summary>
public class OdysseyTests
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
    public async Task charging_accumulates_warp_energy()
    {
        using var runner = new OdysseyRunner();
        await Assert.That(runner.Energy).IsEqualTo(0.0);

        runner.SetCharging(true);
        for (var i = 0; i < 30; i++) runner.TickOnce();

        await Assert.That(runner.Energy).IsGreaterThan(0.0);
        await Assert.That(runner.Energy).IsLessThanOrEqualTo(runner.EnergyToJump);
    }

    [Test]
    public async Task a_charged_warp_resolves_through_the_bus()
    {
        using var runner = new OdysseyRunner();
        ChargeToFull(runner);
        await Assert.That(runner.Energy).IsGreaterThanOrEqualTo(runner.EnergyToJump);

        var hullBefore = runner.Hull;
        runner.RequestWarp();
        for (var i = 0; i < 5; i++) runner.TickOnce(); // roll (tick N) → reactor applies (tick N+1)

        // A resolution reached the owner-reactor: either the sector advanced (success) or the hull
        // took damage (failure). Both prove the intent→system→event→reactor path fired.
        bool advanced = runner.Sector == 1;
        bool damaged = runner.Hull < hullBefore - 1.0; // beyond the tiny per-tick drain
        await Assert.That(advanced || damaged).IsTrue();
        // The jump also drained the drive.
        await Assert.That(runner.Energy).IsLessThan(runner.EnergyToJump);
    }

    [Test]
    public async Task jumping_eventually_reaches_a_higher_sector()
    {
        using var runner = new OdysseyRunner();
        for (var attempt = 0; attempt < 12 && runner.Sector == 0 && !runner.IsDestroyed; attempt++)
        {
            ChargeToFull(runner);
            runner.RequestWarp();
            for (var i = 0; i < 3; i++) runner.TickOnce();
        }
        await Assert.That(runner.Sector).IsGreaterThan(0);
    }

    [Test]
    public async Task a_warp_writes_the_ships_log()
    {
        using var runner = new OdysseyRunner();
        ChargeToFull(runner);
        runner.RequestWarp();
        for (var i = 0; i < 3; i++) runner.TickOnce();

        await Assert.That(runner.Log.Any(line => line.Contains("Warp jump"))).IsTrue();
    }

    [Test]
    public async Task new_voyage_resets_the_ship()
    {
        using var runner = new OdysseyRunner();
        // Advance a sector so there is something to reset.
        for (var attempt = 0; attempt < 12 && runner.Sector == 0 && !runner.IsDestroyed; attempt++)
        {
            ChargeToFull(runner);
            runner.RequestWarp();
            for (var i = 0; i < 3; i++) runner.TickOnce();
        }

        runner.RequestNewVoyage();          // MANAGED bus emit
        for (var i = 0; i < 3; i++) runner.TickOnce(); // emit committed → reactor resets next tick

        await Assert.That(runner.Sector).IsEqualTo(0);
        // Hull restored to full by the reset (then a hair of per-tick drain on the following ticks).
        await Assert.That(runner.Hull).IsGreaterThan(runner.FullHull - 1.0);
        await Assert.That(runner.IsDestroyed).IsFalse();
    }

    [Test]
    public async Task an_undriven_ship_breaches_its_hull()
    {
        using var runner = new OdysseyRunner();
        for (var i = 0; i < 6000 && !runner.IsDestroyed; i++)
        {
            runner.TickOnce(); // never charge — the hull just drains
        }
        await Assert.That(runner.IsDestroyed).IsTrue();
        await Assert.That(runner.Hull).IsEqualTo(0.0);
    }
}
