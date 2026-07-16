namespace ParadiseCultivation.Tests;

/// <summary>Grid travel under the locked rules: four-direction A* with per-terrain day
/// costs, water impassable on foot, sword flight straight over anything, whole-journey
/// round-up with a 1-day minimum, and the WASD single-step path through the runner.</summary>
public class PathfindingTests
{
    private static WorldMap DemoMap(int seed = 12345) =>
        WorldGenerator.Generate(Fixture.Config, seed, presetIndex: 0).Map;

    [Test]
    public async Task walking_paths_are_four_adjacent_walkable_and_cost_terrain_days()
    {
        var map = DemoMap();
        var home = map.Sites[0];
        var target = map.Sites[1];

        var plan = Pathfinding.Plan(Fixture.Config, map, home.X, home.Y, realmIndex: 0, target.X, target.Y);

        await Assert.That(plan).IsNotNull();
        await Assert.That(plan!.Mode).IsEqualTo(Fixture.Config.Text.Messages.WalkMode);
        var (px, py) = (home.X, home.Y);
        var expectedDays = 0.0;
        foreach (var (x, y) in plan.Steps)
        {
            await Assert.That(Math.Abs(x - px) + Math.Abs(y - py)).IsEqualTo(1); // 4-adjacency
            var cost = Pathfinding.FootCost(Fixture.Config, map.TileAt(x, y));
            await Assert.That(cost).IsGreaterThan(0f); // never steps into water
            expectedDays += cost;
            (px, py) = (x, y);
        }
        await Assert.That((px, py)).IsEqualTo((target.X, target.Y));
        await Assert.That(Math.Abs(plan.CumulativeDays[^1] - expectedDays)).IsLessThan(1e-3);
        await Assert.That(plan.TotalDays).IsEqualTo(Math.Max(1, (int)Math.Ceiling(expectedDays)));
    }

    [Test]
    public async Task water_is_impassable_on_foot_but_flight_crosses_it()
    {
        var map = DemoMap();
        var water = FindTile(map, t => t.Terrain == Terrain.Water);
        var home = map.Sites[0];

        var walk = Pathfinding.Plan(Fixture.Config, map, home.X, home.Y, realmIndex: 0, water.X, water.Y);
        await Assert.That(walk).IsNull(); // a water destination has no foot path

        var flight = Pathfinding.Plan(
            Fixture.Config, map, home.X, home.Y, Fixture.Config.Time.SwordFlightRealmIndex, water.X, water.Y);
        await Assert.That(flight).IsNotNull();
        await Assert.That(flight!.Mode).IsEqualTo(Fixture.Config.Text.Messages.FlightMode);
    }

    [Test]
    public async Task flight_is_faster_than_walking_over_distance()
    {
        var map = DemoMap();
        var home = map.Sites[0];
        var target = map.Sites[1];

        var walk = Pathfinding.Plan(Fixture.Config, map, home.X, home.Y, 0, target.X, target.Y);
        var flight = Pathfinding.Plan(
            Fixture.Config, map, home.X, home.Y, Fixture.Config.Time.SwordFlightRealmIndex, target.X, target.Y);

        await Assert.That(flight!.TotalDays).IsLessThan(walk!.TotalDays);
        await Assert.That(flight.TotalDays).IsGreaterThanOrEqualTo(1); // round-up, min 1 day
    }

    [Test]
    public async Task runner_travel_walks_the_path_and_lands_exactly()
    {
        using var runner = Fixture.NewRunner();
        var map = runner.Map;
        var target = map.Sites[1];
        var dayBefore = runner.Day;
        var start = runner.Current.GetComponent<PlayerData>(runner.Player);
        var plan = Pathfinding.Plan(Fixture.Config, map, start.X, start.Y, 0, target.X, target.Y)!;

        runner.RequestTravel(target.X, target.Y);
        runner.TickOnce();
        await Assert.That(runner.Busy).IsTrue();

        // Mid-journey the player is ON the path, not teleported to either end.
        var seenIntermediate = false;
        for (var i = 0; i < 20_000 && runner.Busy; i++)
        {
            runner.TickOnce();
            var p = runner.Current.GetComponent<PlayerData>(runner.Player);
            if ((p.X, p.Y) != (start.X, start.Y) && (p.X, p.Y) != (target.X, target.Y))
            {
                seenIntermediate = true;
            }
        }

        await Assert.That(seenIntermediate).IsTrue();
        var arrived = runner.Current.GetComponent<PlayerData>(runner.Player);
        await Assert.That((arrived.X, arrived.Y)).IsEqualTo((target.X, target.Y));
        await Assert.That(runner.Day).IsEqualTo(dayBefore + plan.TotalDays);
        await Assert.That(runner.LastActionResult).Contains(Fixture.Skeleton(Fixture.Config.Text.Messages.ArrivedAtMsg));
    }

    [Test]
    public async Task wasd_step_moves_one_tile_and_refuses_water()
    {
        using var runner = Fixture.NewRunner();
        var map = runner.Map;
        var start = runner.Current.GetComponent<PlayerData>(runner.Player);

        // Find a walkable cardinal neighbor and a step cost to check the day charge.
        (int Dx, int Dy)? walkable = null;
        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            if (map.InBounds(start.X + dx, start.Y + dy) &&
                Pathfinding.IsWalkable(Fixture.Config, map.TileAt(start.X + dx, start.Y + dy)))
            {
                walkable = (dx, dy);
                break;
            }
        }
        await Assert.That(walkable.HasValue).IsTrue();

        var dayBefore = runner.Day;
        runner.RequestTravelStep(walkable!.Value.Dx, walkable.Value.Dy);
        Fixture.RunUntilIdle(runner);

        var stepped = runner.Current.GetComponent<PlayerData>(runner.Player);
        await Assert.That((stepped.X, stepped.Y))
            .IsEqualTo((start.X + walkable.Value.Dx, start.Y + walkable.Value.Dy));
        var cost = Pathfinding.FootCost(Fixture.Config, map.TileAt(stepped.X, stepped.Y));
        await Assert.That(runner.Day).IsEqualTo(dayBefore + Math.Max(1, (int)Math.Ceiling(cost)));

        // Diagonal steps are rejected outright (four-direction adjacency).
        var before = (stepped.X, stepped.Y);
        runner.RequestTravelStep(1, 1);
        runner.TickOnce();
        var after = runner.Current.GetComponent<PlayerData>(runner.Player);
        await Assert.That((after.X, after.Y)).IsEqualTo(before);
        await Assert.That(runner.Busy).IsFalse();
    }

    private static (int X, int Y) FindTile(WorldMap map, Func<Tile, bool> predicate)
    {
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                if (predicate(map.TileAt(x, y))) return (x, y);
            }
        }
        throw new InvalidOperationException("no matching tile in the test world");
    }
}
