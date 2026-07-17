namespace ParadiseCultivation.Tests;

/// <summary>The quiet periodic autosave: fires only while the world is idle, at most once
/// per the config cadence, never clobbers the action-result line, produces a loadable save,
/// and stays off when disabled.</summary>
public class AutosaveTests
{
    private static CultivationRunner TempRunner(out string root)
    {
        root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"cultivation-autosave-{Guid.NewGuid():N}")).FullName;
        var runner = Fixture.NewRunner();
        runner.SaveRoot = root;
        return runner;
    }

    [Test]
    public async Task idle_months_past_the_cadence_write_a_quiet_autosave()
    {
        var runner = TempRunner(out var root);
        try
        {
            using var _ = runner;
            var cadence = Fixture.Config.Save.AutosaveMonths;
            await Assert.That(cadence).IsGreaterThan(0); // the shipped config enables it

            runner.RequestCultivate(cadence + 1);
            Fixture.RunUntilIdle(runner);
            var actionResult = runner.LastActionResult;
            runner.TickOnce(); // the idle tick after completion fires the autosave

            await Assert.That(File.Exists(runner.AutosavePath)).IsTrue();
            await Assert.That(runner.LastAutosaveDay).IsGreaterThanOrEqualTo(0);
            // Quiet: the player's action-result line survives the autosave.
            await Assert.That(runner.LastActionResult).IsEqualTo(actionResult);

            // The cadence gates a second one: the next idle tick must not re-save.
            var stamp = File.GetLastWriteTimeUtc(runner.AutosavePath);
            runner.TickOnce();
            await Assert.That(File.GetLastWriteTimeUtc(runner.AutosavePath)).IsEqualTo(stamp);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task the_autosave_is_a_loadable_save()
    {
        var runner = TempRunner(out var root);
        try
        {
            using var _ = runner;
            var cadence = Fixture.Config.Save.AutosaveMonths;
            runner.RequestCultivate(cadence + 1);
            Fixture.RunUntilIdle(runner);
            runner.TickOnce();
            var savedDay = runner.LastAutosaveDay;

            runner.RequestCultivate(3); // march past the saved moment, then rewind
            Fixture.RunUntilIdle(runner);
            runner.RequestLoad(runner.AutosavePath);
            runner.TickOnce();

            await Assert.That(runner.Day).IsEqualTo(savedDay);
            await Assert.That(runner.Phase).IsEqualTo(GamePhase.Playing);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task loading_restarts_the_cadence_instead_of_saving_immediately()
    {
        var runner = TempRunner(out var root);
        try
        {
            using var _ = runner;
            var cadence = Fixture.Config.Save.AutosaveMonths;
            runner.RequestCultivate(cadence + 1);
            Fixture.RunUntilIdle(runner);
            runner.TickOnce();

            runner.RequestLoad(runner.AutosavePath);
            runner.TickOnce();
            var stamp = File.GetLastWriteTimeUtc(runner.AutosavePath);

            // Freshly loaded: idle ticks alone (no months passing) must not overwrite it.
            for (var i = 0; i < 10; i++) runner.TickOnce();
            await Assert.That(File.GetLastWriteTimeUtc(runner.AutosavePath)).IsEqualTo(stamp);
            await Assert.That(runner.LastAutosaveDay).IsEqualTo(-1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
