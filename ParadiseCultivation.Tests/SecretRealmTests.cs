using System.Text.Json.Nodes;

namespace ParadiseCultivation.Tests;

/// <summary>The secret realm (P2): a deterministic seed-derived schedule of openings, the
/// rumor mechanic that reveals the location through chat, the once-per-opening
/// fortune-weighted trial, the chronicle announcement, and the v4 save knowledge fields
/// (with v3 saves loading unaware).</summary>
public class SecretRealmTests
{
    // Component pokes live in non-async helpers — ref locals are not allowed in async tests.

    private static void MoveTo(CultivationRunner runner, int x, int y)
    {
        ref var player = ref runner.Current.GetComponent<PlayerData>(runner.Player);
        player.X = x;
        player.Y = y;
    }

    private static PlayerData Player(CultivationRunner runner) =>
        runner.Current.GetComponent<PlayerData>(runner.Player);

    /// <summary>A runner advanced into the first opening's window (cultivating up to the
    /// first open month).</summary>
    private static CultivationRunner RunnerInWindow(int seed = 12345)
    {
        var runner = Fixture.NewRunner(seed);
        runner.RequestCultivate(Fixture.Config.SecretRealm.FirstOpenMonth);
        Fixture.RunUntilIdle(runner);
        return runner;
    }

    [Test]
    public async Task the_schedule_is_deterministic_and_windowed()
    {
        var config = Fixture.Config;
        var (map, _) = WorldGenerator.Generate(config, seed: 4242, presetIndex: 0);
        var daysPerMonth = config.Time.DaysPerMonth;
        var openDay = config.SecretRealm.FirstOpenMonth * daysPerMonth;

        await Assert.That(SecretRealms.TryGetCurrent(config, map, openDay - 1).HasValue).IsFalse();

        var realm = SecretRealms.TryGetCurrent(config, map, openDay);
        await Assert.That(realm.HasValue).IsTrue();
        await Assert.That(realm!.Value.Index).IsEqualTo(0L);
        await Assert.That(realm.Value).IsEqualTo(SecretRealms.TryGetCurrent(config, map, openDay)!.Value);

        // The location is walkable, site-free, and stable across the whole window.
        var tile = map.TileAt(realm.Value.X, realm.Value.Y);
        await Assert.That(Pathfinding.IsWalkable(config, tile)).IsTrue();
        await Assert.That((int)tile.SiteIndex).IsEqualTo(-1);
        var lastOpenDay = openDay + config.SecretRealm.OpenMonths * daysPerMonth - 1;
        await Assert.That(SecretRealms.TryGetCurrent(config, map, lastOpenDay)!.Value.X).IsEqualTo(realm.Value.X);

        // Closed right after the window; the NEXT period is a different opening index.
        await Assert.That(SecretRealms.TryGetCurrent(config, map, lastOpenDay + 1).HasValue).IsFalse();
        var nextOpenDay = (config.SecretRealm.FirstOpenMonth + config.SecretRealm.PeriodMonths) * daysPerMonth;
        await Assert.That(SecretRealms.TryGetCurrent(config, map, nextOpenDay)!.Value.Index).IsEqualTo(1L);
    }

    [Test]
    public async Task an_opening_is_announced_in_the_chronicle()
    {
        using var runner = RunnerInWindow();
        var skeleton = Fixture.Skeleton(Fixture.Config.Text.Messages.RealmOpenLog);
        await Assert.That(runner.Chronicle.Any(entry => entry.Summary.Contains(skeleton))).IsTrue();
    }

    [Test]
    public async Task rumor_talk_reveals_the_realm_and_lights_the_marker()
    {
        using var runner = RunnerInWindow();
        var npc = Fixture.FirstNpcAtPlayerSite(runner);

        // Ordinary small talk reveals nothing.
        runner.RequestChat(npc, "今日天气不错。");
        Fixture.RunUntilIdle(runner);
        await Assert.That(runner.KnownRealmIndex).IsEqualTo(-1L);

        // Asking for rumors names the realm, bearing, and deadline.
        runner.RequestChat(npc, $"最近江湖上有什么{Fixture.Config.SecretRealm.RumorKeywords[0]}？");
        Fixture.RunUntilIdle(runner);
        await Assert.That(runner.KnownRealmIndex).IsEqualTo(0L);
        await Assert.That(runner.LastReply)
            .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.RumorRealmMsg));
        await Assert.That(runner.LastReply)
            .Contains(SecretRealms.NameOf(Fixture.Config, 0));
    }

    [Test]
    public async Task the_trial_pays_out_or_injures_and_spends_the_opening()
    {
        using var runner = RunnerInWindow();
        var realm = SecretRealms.TryGetCurrent(Fixture.Config, runner.Map, runner.Day)!.Value;
        MoveTo(runner, realm.X, realm.Y);
        var before = Player(runner);
        var cultivationBefore = runner.Current.GetComponent<Cultivator>(runner.Player).CultivationPoints;

        runner.RequestEnterRealm();
        Fixture.RunUntilIdle(runner);

        var after = Player(runner);
        var messages = Fixture.Config.Text.Messages;
        if (runner.LastActionResult.Contains(Fixture.Skeleton(messages.RealmSuccessMsg)))
        {
            await Assert.That(after.SpiritStones).IsGreaterThan(before.SpiritStones);
            await Assert.That(after.Herbs).IsGreaterThan(before.Herbs);
            await Assert.That(after.Fortune > before.Fortune).IsTrue();
        }
        else
        {
            await Assert.That(runner.LastActionResult).Contains(Fixture.Skeleton(messages.RealmFailMsg));
            await Assert.That(after.InjuryMonths)
                .IsEqualTo(before.InjuryMonths + Fixture.Config.SecretRealm.FailureInjuryMonths);
        }
        await Assert.That(runner.LastRealmTrialIndex).IsEqualTo(realm.Index);

        // The attempt spends the opening, win or lose.
        runner.RequestEnterRealm();
        runner.TickOnce();
        await Assert.That(runner.LastActionResult).IsEqualTo(messages.RealmSpentMsg);
    }

    [Test]
    public async Task entering_needs_an_open_realm_underfoot()
    {
        // In the window but standing elsewhere: refused.
        using var inWindow = RunnerInWindow();
        inWindow.RequestEnterRealm();
        inWindow.TickOnce();
        await Assert.That(inWindow.LastActionResult).IsEqualTo(Fixture.Config.Text.Messages.RealmNotHereMsg);

        // On the right tile but before the window: refused (nothing there yet).
        using var early = Fixture.NewRunner();
        var futureRealm = SecretRealms.LocationOf(Fixture.Config, early.Map, 0);
        MoveTo(early, futureRealm.X, futureRealm.Y);
        early.RequestEnterRealm();
        early.TickOnce();
        await Assert.That(early.LastActionResult).IsEqualTo(Fixture.Config.Text.Messages.RealmNotHereMsg);
    }

    [Test]
    public async Task realm_knowledge_rides_the_save_and_v3_loads_unaware()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cultrealm_{Guid.NewGuid():N}.json");
        try
        {
            using var runner = RunnerInWindow();
            var npc = Fixture.FirstNpcAtPlayerSite(runner);
            runner.RequestChat(npc, Fixture.Config.SecretRealm.RumorKeywords[0]);
            Fixture.RunUntilIdle(runner);
            runner.RequestSave(path);
            runner.TickOnce();

            using var restored = new CultivationRunner(Fixture.Config, seed: 1, presetIndex: 0);
            restored.RequestLoad(path);
            restored.TickOnce();
            await Assert.That(restored.KnownRealmIndex).IsEqualTo(0L);

            // Strip the v4 fields: a v3 save loads with no realm knowledge.
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["version"] = 3;
            node.Remove("knownRealmIndex");
            node.Remove("lastRealmTrialIndex");
            File.WriteAllText(path, node.ToJsonString());

            using var v3 = new CultivationRunner(Fixture.Config, seed: 1, presetIndex: 0);
            v3.RequestLoad(path);
            v3.TickOnce();
            await Assert.That(v3.LastActionResult)
                .Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.LoadDoneMsg));
            await Assert.That(v3.KnownRealmIndex).IsEqualTo(-1L);
            await Assert.That(v3.LastRealmTrialIndex).IsEqualTo(-1L);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
