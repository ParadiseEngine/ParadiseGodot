namespace ParadiseCultivation.Tests;

/// <summary>The endgame: ascension gates on the final realm's Perfected peak with a full
/// bar, the tribulation is one fortune-weighted roll, success ends the run in the Ascended
/// phase (a win state, mirroring death), and failure burns the bar + injures — retryable.</summary>
public class AscensionTests
{
    // Component pokes live in non-async helpers — ref locals are not allowed in async tests.

    private static void PokeToLaddersTop(CultivationRunner runner, float fortune)
    {
        ref var cultivator = ref runner.Current.GetComponent<Cultivator>(runner.Player);
        cultivator.RealmIndex = Fixture.Config.Realms.Length - 1;
        cultivator.SubStage = Fixture.Config.SubStages.Length - 1;
        cultivator.CultivationPoints = Fixture.Config.Realms[^1].PointsPerSubStage;
        ref var player = ref runner.Current.GetComponent<PlayerData>(runner.Player);
        player.Fortune = fortune;
        player.LifespanYears = 1_000_000; // the trial's failed attempts must not age him out
    }

    private static void RefillBar(CultivationRunner runner) =>
        runner.Current.GetComponent<Cultivator>(runner.Player).CultivationPoints =
            Fixture.Config.Realms[^1].PointsPerSubStage;

    private static Cultivator PlayerCultivator(CultivationRunner runner) =>
        runner.Current.GetComponent<Cultivator>(runner.Player);

    private static PlayerData Player(CultivationRunner runner) =>
        runner.Current.GetComponent<PlayerData>(runner.Player);

    private static float ChanceAtFortune(float fortune)
    {
        var player = new PlayerData { Fortune = fortune };
        return CultivationRules.AscensionChance(Fixture.Config, in player);
    }

    [Test]
    public async Task the_chance_is_fortune_weighted_and_clamped()
    {
        await Assert.That(ChanceAtFortune(0))
            .IsEqualTo(Math.Clamp(Fixture.Config.Ascension.BaseChance, 0.05f, 0.95f)).Within(1e-4f);
        await Assert.That(ChanceAtFortune(100_000)).IsEqualTo(0.95f);
        await Assert.That(ChanceAtFortune(-100_000)).IsEqualTo(0.05f);
    }

    [Test]
    public async Task ascension_gates_on_the_ladders_top()
    {
        using var runner = Fixture.NewRunner();
        var cultivator = PlayerCultivator(runner); // realm 0 — nowhere near
        await Assert.That(CultivationRules.AscensionReady(Fixture.Config, in cultivator)).IsFalse();

        runner.RequestAscend();
        runner.TickOnce();

        await Assert.That(runner.LastActionResult).IsEqualTo(Fixture.Config.Text.Messages.AscendNotReadyMsg);
        await Assert.That(runner.Phase).IsEqualTo(GamePhase.Playing);
        await Assert.That(runner.Busy).IsFalse(); // refusals cost no time
    }

    [Test]
    public async Task the_final_tribulation_ends_the_journey_in_victory()
    {
        using var runner = Fixture.NewRunner();
        PokeToLaddersTop(runner, fortune: 100_000); // chance clamps to 0.95

        for (var attempt = 0; attempt < 200 && runner.Phase != GamePhase.Ascended; attempt++)
        {
            RefillBar(runner); // a failed trial burns the bar; climb again
            runner.RequestAscend();
            Fixture.RunUntilIdle(runner);
        }

        await Assert.That(runner.Phase).IsEqualTo(GamePhase.Ascended);
        await Assert.That(runner.Chronicle.Any(entry => entry.Summary.Contains(
            Fixture.Skeleton(Fixture.Config.Text.Messages.AscendLog)))).IsTrue();

        // The journey is over: commands fall on deaf ears, the world stands still.
        var day = runner.Day;
        runner.RequestCultivate(3);
        runner.TickOnce();
        await Assert.That(runner.Busy).IsFalse();
        await Assert.That(runner.Day).IsEqualTo(day);
        await Assert.That(runner.Phase).IsEqualTo(GamePhase.Ascended);
    }

    [Test]
    public async Task a_failed_tribulation_burns_the_bar_and_injures()
    {
        using var runner = Fixture.NewRunner();
        PokeToLaddersTop(runner, fortune: -100_000); // chance clamps to 0.05
        var fullBar = Fixture.Config.Realms[^1].PointsPerSubStage;

        var failed = false;
        for (var attempt = 0; attempt < 200 && !failed; attempt++)
        {
            RefillBar(runner);
            runner.Current.GetComponent<PlayerData>(runner.Player).InjuryMonths = 0;
            runner.RequestAscend();
            Fixture.RunUntilIdle(runner);
            failed = runner.Phase == GamePhase.Playing;
            if (runner.Phase == GamePhase.Ascended) return; // the 5% came up — nothing to assert
        }

        await Assert.That(failed).IsTrue();
        await Assert.That(PlayerCultivator(runner).CultivationPoints)
            .IsEqualTo(fullBar * (1.0 - Fixture.Config.Ascension.FailureCultivationLoss)).Within(1.0);
        // The 30-day trial itself crosses one month boundary, healing one month.
        var trialMonths = Fixture.Config.Ascension.TrialDays / Fixture.Config.Time.DaysPerMonth;
        await Assert.That(Player(runner).InjuryMonths)
            .IsEqualTo(Fixture.Config.Ascension.FailureInjuryMonths - trialMonths);
        await Assert.That(runner.Chronicle.Any(entry => entry.Summary.Contains(
            Fixture.Skeleton(Fixture.Config.Text.Messages.AscendFailLog)))).IsTrue();
    }
}
