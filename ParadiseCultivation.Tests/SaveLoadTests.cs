namespace ParadiseCultivation.Tests;

/// <summary>The versioned save system: full round-trip fidelity (player, NPCs, memories,
/// chronicle, calendar, RNG stream), deterministic continuation after load, and the
/// fail-safely contract — corrupt or wrong-version saves must leave the running world
/// untouched.</summary>
public class SaveLoadTests
{
    private static string TempSave() =>
        Path.Combine(Path.GetTempPath(), $"cultsave_{Guid.NewGuid():N}.json");

    [Test]
    public async Task save_and_load_round_trips_the_whole_game_state()
    {
        var path = TempSave();
        try
        {
            using var runner = Fixture.NewRunner(seed: 4242);
            var npc = Fixture.FirstNpcAtPlayerSite(runner);
            runner.RequestChat(npc, "Remember this before the save.");
            Fixture.RunUntilIdle(runner);
            runner.RequestCultivate(2);
            Fixture.RunUntilIdle(runner);

            runner.RequestSave(path);
            runner.TickOnce();
            await Assert.That(runner.LastActionResult).Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.SaveDoneMsg));
            await Assert.That(File.Exists(path)).IsTrue();

            var savedDay = runner.Day;
            var savedPlayer = runner.Current.GetComponent<Cultivator>(runner.Player);
            var savedNpcState = runner.Current.GetComponent<NpcState>(npc);

            using var restored = new CultivationRunner(Fixture.Config, seed: 1, presetIndex: 0);
            restored.RequestLoad(path);
            restored.TickOnce();

            await Assert.That(restored.LastActionResult).Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.LoadDoneMsg));
            await Assert.That(restored.Phase).IsEqualTo(GamePhase.Playing);
            await Assert.That(restored.Day).IsEqualTo(savedDay);
            await Assert.That(restored.Map.Seed).IsEqualTo(4242); // map re-derived from the seed

            var restoredPlayer = restored.Current.GetComponent<Cultivator>(restored.Player);
            await Assert.That(restoredPlayer.CultivationPoints).IsEqualTo(savedPlayer.CultivationPoints);
            await Assert.That(restoredPlayer.AgeDays).IsEqualTo(savedPlayer.AgeDays);

            // The chatted NPC keeps affection AND its memory log across the round trip.
            var restoredNpc = restored.Npcs.First(e =>
                restored.Current.GetComponent<NpcState>(e).NpcId == savedNpcState.NpcId);
            var restoredState = restored.Current.GetComponent<NpcState>(restoredNpc);
            await Assert.That(restoredState.AffectionToPlayer).IsEqualTo(savedNpcState.AffectionToPlayer);
            var memories = restored.MemoriesOf(restoredNpc);
            await Assert.That(memories.Count).IsEqualTo(1);
            await Assert.That(memories[0].Summary).Contains("Remember this before the save.");
            await Assert.That(restored.Chronicle.Count).IsEqualTo(runner.Chronicle.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task loaded_games_continue_the_same_random_stream()
    {
        var path = TempSave();
        try
        {
            using var original = Fixture.NewRunner(seed: 999);
            original.RequestExplore();
            Fixture.RunUntilIdle(original);
            original.RequestSave(path);
            original.TickOnce();

            using var restored = new CultivationRunner(Fixture.Config, seed: 1, presetIndex: 0);
            restored.RequestLoad(path);
            restored.TickOnce();

            // The SAME next action on both must roll the SAME outcomes (PCG state saved).
            original.RequestExplore();
            restored.RequestExplore();
            Fixture.RunUntilIdle(original);
            Fixture.RunUntilIdle(restored);

            await Assert.That(restored.LastActionResult).IsEqualTo(original.LastActionResult);
            var po = original.Current.GetComponent<PlayerData>(original.Player);
            var pr = restored.Current.GetComponent<PlayerData>(restored.Player);
            await Assert.That((pr.SpiritStones, pr.Herbs, pr.Fortune)).IsEqualTo((po.SpiritStones, po.Herbs, po.Fortune));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task corrupt_and_wrong_version_saves_fail_without_touching_the_world()
    {
        using var runner = Fixture.NewRunner(seed: 31337);
        var dayBefore = runner.Day;
        var npcCountBefore = runner.Npcs.Count;

        var corrupt = TempSave();
        var wrongVersion = TempSave();
        try
        {
            File.WriteAllText(corrupt, "{ this is not : json ]");
            runner.RequestLoad(corrupt);
            runner.TickOnce();
            await Assert.That(runner.LastActionResult).Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.LoadFailMsg));
            await Assert.That(runner.Day).IsEqualTo(dayBefore);
            await Assert.That(runner.Npcs.Count).IsEqualTo(npcCountBefore);
            await Assert.That(runner.Phase).IsEqualTo(GamePhase.Playing);

            // A structurally VALID save with a future version — the version gate must fire.
            runner.RequestSave(wrongVersion);
            runner.TickOnce();
            File.WriteAllText(wrongVersion, File.ReadAllText(wrongVersion)
                .Replace($"\"version\": {SaveData.CurrentVersion}", "\"version\": 999"));
            runner.RequestLoad(wrongVersion);
            runner.TickOnce();
            await Assert.That(runner.LastActionResult).Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.LoadVersionMsg));
            await Assert.That(runner.Phase).IsEqualTo(GamePhase.Playing);

            runner.RequestLoad("/nonexistent/save.json");
            runner.TickOnce();
            await Assert.That(runner.LastActionResult).Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.LoadFailMsg));
            await Assert.That(runner.ThreadException).IsNull();
        }
        finally
        {
            File.Delete(corrupt);
            File.Delete(wrongVersion);
        }
    }

    [Test]
    public async Task saving_is_refused_while_time_flows()
    {
        var path = TempSave();
        try
        {
            using var runner = Fixture.NewRunner();
            runner.RequestSeclude(2);
            runner.TickOnce(); // busy now
            runner.RequestSave(path);
            runner.TickOnce();

            await Assert.That(runner.LastActionResult).IsEqualTo(Fixture.Config.Text.Messages.SaveBusyMsg);
            await Assert.That(File.Exists(path)).IsFalse();
            Fixture.RunUntilIdle(runner);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task pcg_state_round_trips_exactly()
    {
        var rng = new Pcg32(seed: 7);
        for (var i = 0; i < 100; i++) rng.NextUInt();

        var restored = new Pcg32(rng.State, rng.Stream);
        for (var i = 0; i < 100; i++)
        {
            await Assert.That(restored.NextUInt()).IsEqualTo(rng.NextUInt());
        }

        var a = new Pcg32(seed: 7);
        var b = new Pcg32(seed: 7);
        for (var i = 0; i < 100; i++)
        {
            await Assert.That(b.NextUInt()).IsEqualTo(a.NextUInt());
        }
        var d = new Pcg32(seed: 8);
        var same = 0;
        var aa = new Pcg32(seed: 7);
        for (var i = 0; i < 100; i++)
        {
            if (aa.NextUInt() == d.NextUInt()) same++;
        }
        await Assert.That(same).IsLessThan(5); // different seeds diverge
    }
}
