namespace ParadiseCultivation;

/// <summary>A planned journey: the tile sequence (excluding the start tile), the cumulative
/// day cost at each step (rounded up per whole journey, per-step fractional for animation),
/// and the movement mode.</summary>
public sealed class TravelPlan
{
    public required IReadOnlyList<(int X, int Y)> Steps { get; init; }
    /// <summary>Fractional days to REACH each corresponding step from the journey start.</summary>
    public required IReadOnlyList<double> CumulativeDays { get; init; }
    /// <summary>Whole-journey cost: rounded up, minimum 1 day (design rule).</summary>
    public required int TotalDays { get; init; }
    public required string Mode { get; init; }
}

/// <summary>
/// Grid travel per the locked design: square grid, FOUR-direction adjacency, terrain-derived
/// day costs (config <c>terrain.moveCostDays</c>, ≤0 = impassable on foot — Water), A* on
/// foot; sword flight goes point-to-point over anything at the flight speed. Costs round up
/// to whole days, minimum 1 (only high-realm void tearing would be 0 — not implemented).
/// </summary>
public static class Pathfinding
{
    private static readonly (int Dx, int Dy)[] Directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    public static bool IsWalkable(CultivationConfig config, in Tile tile) =>
        config.World.Terrain.MoveCostDays[(int)tile.Terrain] > 0f;

    public static float FootCost(CultivationConfig config, in Tile tile) =>
        config.World.Terrain.MoveCostDays[(int)tile.Terrain];

    /// <summary>Plan a journey; null when unreachable (or out of bounds / same tile).
    /// Flight (realm ≥ swordFlightRealmIndex) flies the straight grid line over any terrain;
    /// walking runs A* over walkable tiles with per-terrain day costs.</summary>
    public static TravelPlan? Plan(
        CultivationConfig config, WorldMap map, int fromX, int fromY, int realmIndex, int toX, int toY)
    {
        if (!map.InBounds(toX, toY) || (fromX == toX && fromY == toY)) return null;

        // No stopping on water for ANY mode (inland lakes, no boats): flight crosses it but
        // may not end on it — otherwise a flyer parks mid-lake, which the design never meant.
        if (!IsWalkable(config, map.TileAt(toX, toY))) return null;

        if (realmIndex >= config.Time.SwordFlightRealmIndex)
        {
            return PlanFlight(config, fromX, fromY, toX, toY);
        }

        return PlanWalk(config, map, fromX, fromY, toX, toY);
    }

    private static TravelPlan PlanFlight(CultivationConfig config, int fromX, int fromY, int toX, int toY)
    {
        var steps = new List<(int, int)>();
        var days = new List<double>();
        var daysPerTile = 1.0 / config.Time.SwordFlightTilesPerDay;

        // Straight grid line (Bresenham) — flight ignores terrain entirely.
        int x = fromX, y = fromY;
        var dx = Math.Abs(toX - fromX);
        var dy = -Math.Abs(toY - fromY);
        var sx = fromX < toX ? 1 : -1;
        var sy = fromY < toY ? 1 : -1;
        var err = dx + dy;
        var traveled = 0;
        while (x != toX || y != toY)
        {
            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
            traveled++;
            steps.Add((x, y));
            days.Add(traveled * daysPerTile);
        }

        return new TravelPlan
        {
            Steps = steps,
            CumulativeDays = days,
            TotalDays = Math.Max(1, (int)Math.Ceiling(days[^1])),
            Mode = config.Text.Messages.FlightMode,
        };
    }

    private static TravelPlan? PlanWalk(CultivationConfig config, WorldMap map, int fromX, int fromY, int toX, int toY)
    {
        var width = map.Width;
        var size = width * map.Height;
        var best = new float[size]; // planning is not per-frame hot; keep it off the stack
        Array.Fill(best, float.PositiveInfinity);
        var cameFrom = new int[size];
        Array.Fill(cameFrom, -1);

        // A* with a manhattan-distance × cheapest-terrain heuristic (admissible).
        var cheapest = float.PositiveInfinity;
        foreach (var cost in config.World.Terrain.MoveCostDays)
        {
            if (cost > 0f) cheapest = Math.Min(cheapest, cost);
        }

        var open = new PriorityQueue<int, float>();
        var start = fromY * width + fromX;
        var goal = toY * width + toX;
        best[start] = 0f;
        open.Enqueue(start, Heuristic(fromX, fromY));

        while (open.TryDequeue(out var current, out _))
        {
            if (current == goal) break;
            var cx = current % width;
            var cy = current / width;
            foreach (var (dx, dy) in Directions)
            {
                var nx = cx + dx;
                var ny = cy + dy;
                if (!map.InBounds(nx, ny)) continue;
                ref readonly var tile = ref map.TileAt(nx, ny);
                var stepCost = FootCost(config, in tile);
                if (stepCost <= 0f) continue; // impassable (Water)
                var next = ny * width + nx;
                var g = best[current] + stepCost;
                if (g < best[next])
                {
                    best[next] = g;
                    cameFrom[next] = current;
                    open.Enqueue(next, g + Heuristic(nx, ny));
                }
            }
        }

        if (float.IsPositiveInfinity(best[goal])) return null;

        var steps = new List<(int, int)>();
        var days = new List<double>();
        for (var node = goal; node != start; node = cameFrom[node])
        {
            steps.Add((node % width, node / width));
            days.Add(best[node]);
        }
        steps.Reverse();
        days.Reverse();

        return new TravelPlan
        {
            Steps = steps,
            CumulativeDays = days,
            TotalDays = Math.Max(1, (int)Math.Ceiling(days[^1])),
            Mode = config.Text.Messages.WalkMode,
        };

        float Heuristic(int x, int y) => (Math.Abs(x - toX) + Math.Abs(y - toY)) * cheapest;
    }

    /// <summary>Every walkable tile reachable on foot from (x, y) — generation validation
    /// uses this to guarantee all sites share one landmass (no boats by design).</summary>
    public static bool[] WalkableRegion(CultivationConfig config, WorldMap map, int fromX, int fromY)
    {
        var width = map.Width;
        var visited = new bool[width * map.Height];
        var queue = new Queue<int>();
        var start = fromY * width + fromX;
        visited[start] = true;
        queue.Enqueue(start);
        while (queue.TryDequeue(out var current))
        {
            var cx = current % width;
            var cy = current / width;
            foreach (var (dx, dy) in Directions)
            {
                var nx = cx + dx;
                var ny = cy + dy;
                if (!map.InBounds(nx, ny)) continue;
                var next = ny * width + nx;
                if (visited[next] || !IsWalkable(config, map.TileAt(nx, ny))) continue;
                visited[next] = true;
                queue.Enqueue(next);
            }
        }
        return visited;
    }
}
