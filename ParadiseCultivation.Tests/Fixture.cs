using Paradise.ECS;

namespace ParadiseCultivation.Tests;

/// <summary>Shared access to the SHIPPED config (copied next to the test assembly) — tests
/// validate the authored numbers the game actually runs with. Runners are driven
/// SYNCHRONOUSLY via <see cref="CultivationRunner.TickOnce"/> (never Start), the same pattern
/// as ParadiseGame's PoolGameTests, so world pokes between ticks and side-store reads are
/// single-threaded and safe. The generated `World` alias exists only inside ParadiseCultivation
/// (per-assembly source gen — see .claude/lessons.md), so tests receive worlds via
/// <c>runner.Current</c> / <c>out var</c> and never name the type.</summary>
internal static class Fixture
{
    private static readonly Lazy<CultivationConfig> Cached = new(() =>
        CultivationConfig.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config.json"))));

    public static CultivationConfig Config => Cached.Value;

    /// <summary>A runner already past the new-game screen (BeginJourney processed).</summary>
    public static CultivationRunner NewRunner(int seed = 12345, int sizeIndex = 0)
    {
        var runner = new CultivationRunner(Config, seed, sizeIndex);
        runner.RequestBeginJourney();
        runner.TickOnce();
        return runner;
    }

    /// <summary>Tick until the pending time advance completes (bounded — a stuck advance
    /// fails the test instead of hanging it).</summary>
    public static void RunUntilIdle(CultivationRunner runner, int maxTicks = 20_000)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            runner.TickOnce();
            if (!runner.Busy) return;
        }
        throw new InvalidOperationException($"still busy after {maxTicks} ticks");
    }

    public static Entity FirstNpcAtPlayerSite(CultivationRunner runner)
    {
        var world = runner.Current;
        var player = world.GetComponent<PlayerData>(runner.Player);
        var site = runner.Map.TileAt(player.X, player.Y).SiteIndex;
        foreach (var entity in runner.Npcs)
        {
            var npc = world.GetComponent<NpcState>(entity);
            if (npc.Alive != 0 && npc.SiteIndex == site) return entity;
        }
        throw new InvalidOperationException("no living NPC at the player's site");
    }
}
