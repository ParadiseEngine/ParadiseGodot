namespace ParadiseCultivation.Tests;

/// <summary>The random-event schedule: pure hash of (world seed, month) — deterministic per
/// seed, nothing saved — whose config multipliers bend exactly one rules quantity for the
/// event month, and whose authored line enters the chronicle on the crossing.</summary>
public class WorldEventTests
{
    private static long FindEventMonth(int worldSeed, WorldEventEffect effect, long fromMonth = 1)
    {
        for (var month = fromMonth; month < 100_000; month++)
        {
            if (WorldEvents.TryGetForMonth(Fixture.Config, worldSeed, month) is { } info &&
                info.Effect == effect)
            {
                return month;
            }
        }
        throw new InvalidOperationException($"no {effect} event within 100000 months");
    }

    private static long FindQuietMonth(int worldSeed)
    {
        for (var month = 1L; month < 100_000; month++)
        {
            if (WorldEvents.TryGetForMonth(Fixture.Config, worldSeed, month) is null)
            {
                return month;
            }
        }
        throw new InvalidOperationException("no quiet month within 100000 months");
    }

    [Test]
    public async Task the_schedule_is_deterministic_and_respects_the_first_event_month()
    {
        var cfg = Fixture.Config.WorldEvents;
        for (var month = 0L; month < cfg.FirstEventMonth; month++)
        {
            await Assert.That(WorldEvents.TryGetForMonth(Fixture.Config, 777, month)).IsNull();
        }

        var eventCount = 0;
        for (var month = 0L; month < 1000; month++)
        {
            var a = WorldEvents.TryGetForMonth(Fixture.Config, 777, month);
            var b = WorldEvents.TryGetForMonth(Fixture.Config, 777, month);
            await Assert.That(a).IsEqualTo(b);
            if (a is { } info)
            {
                eventCount++;
                await Assert.That(info.ArchetypeIndex).IsGreaterThanOrEqualTo(0);
                await Assert.That(info.ArchetypeIndex).IsLessThan(cfg.Archetypes.Length);
                await Assert.That(info.Magnitude)
                    .IsEqualTo(cfg.Archetypes[info.ArchetypeIndex].Magnitude);
            }
        }
        // ~MonthlyChancePercent of 1000 months host an event; generous tolerance, the point
        // is "neither never nor always".
        await Assert.That(eventCount).IsGreaterThan(0);
        await Assert.That(eventCount).IsLessThan(1000);
    }

    [Test]
    public async Task multipliers_bend_exactly_their_own_quantity()
    {
        const int seed = 424242;
        var eventMonth = FindEventMonth(seed, WorldEventEffect.CultivationGain);
        var eventDay = eventMonth * Fixture.Config.Time.DaysPerMonth;
        var quietDay = FindQuietMonth(seed) * Fixture.Config.Time.DaysPerMonth;
        var magnitude = WorldEvents.TryGetCurrent(Fixture.Config, seed, eventDay)!.Value.Magnitude;

        await Assert.That(WorldEvents.Multiplier(
            Fixture.Config, seed, eventDay, WorldEventEffect.CultivationGain)).IsEqualTo(magnitude);
        // The same event leaves every OTHER quantity alone.
        await Assert.That(WorldEvents.Multiplier(
            Fixture.Config, seed, eventDay, WorldEventEffect.HerbPrice)).IsEqualTo(1f);
        await Assert.That(WorldEvents.Multiplier(
            Fixture.Config, seed, quietDay, WorldEventEffect.CultivationGain)).IsEqualTo(1f);
    }

    [Test]
    public async Task event_months_scale_cultivation_gain_and_market_prices()
    {
        using var runner = Fixture.NewRunner();
        var world = runner.Current;
        var seed = runner.Map.GenerationSeed;
        var cultivator = world.GetComponent<Cultivator>(runner.Player);
        var player = world.GetComponent<PlayerData>(runner.Player);
        var siteIndex = runner.Map.TileAt(player.X, player.Y).SiteIndex;

        var daysPerMonth = Fixture.Config.Time.DaysPerMonth;
        var quietDay = FindQuietMonth(seed) * daysPerMonth;

        var gainDay = FindEventMonth(seed, WorldEventEffect.CultivationGain) * daysPerMonth;
        var gainMagnitude = WorldEvents.TryGetCurrent(Fixture.Config, seed, gainDay)!.Value.Magnitude;
        var quietGain = CultivationRules.MonthlyCultivationGain(
            Fixture.Config, runner.Map, in cultivator, in player, quietDay);
        var eventGain = CultivationRules.MonthlyCultivationGain(
            Fixture.Config, runner.Map, in cultivator, in player, gainDay);
        await Assert.That(eventGain / quietGain).IsEqualTo((double)gainMagnitude).Within(1e-4);

        var herbDay = FindEventMonth(seed, WorldEventEffect.HerbPrice) * daysPerMonth;
        var herbMagnitude = WorldEvents.TryGetCurrent(Fixture.Config, seed, herbDay)!.Value.Magnitude;
        var quietHerb = CultivationRules.HerbSellStones(Fixture.Config, runner.Map, siteIndex, quietDay);
        var eventHerb = CultivationRules.HerbSellStones(Fixture.Config, runner.Map, siteIndex, herbDay);
        await Assert.That(eventHerb).IsEqualTo(Math.Max(1, (int)MathF.Round(
            Fixture.Config.Trade.HerbSellStones
            * CultivationRules.TownPriceMultiplier(Fixture.Config, runner.Map, siteIndex)
            * herbMagnitude)));
        await Assert.That(eventHerb).IsNotEqualTo(quietHerb);
    }

    [Test]
    public async Task event_months_enter_the_chronicle_on_the_crossing()
    {
        using var runner = Fixture.NewRunner();
        var seed = runner.Map.GenerationSeed;
        long eventMonth = -1;
        for (var month = 1L; month < 600; month++)
        {
            if (WorldEvents.TryGetForMonth(Fixture.Config, seed, month) is not null)
            {
                eventMonth = month;
                break;
            }
        }
        await Assert.That(eventMonth).IsGreaterThan(0);

        runner.RequestCultivate((int)eventMonth);
        Fixture.RunUntilIdle(runner);

        var info = WorldEvents.TryGetForMonth(Fixture.Config, seed, eventMonth)!.Value;
        var expected = CultivationRules.F(
            Fixture.Config.Text.Messages.WorldEventLog, info.Name, info.LogLine);
        await Assert.That(runner.Chronicle.Any(entry => entry.Summary == expected)).IsTrue();
    }
}
