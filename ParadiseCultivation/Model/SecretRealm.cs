namespace ParadiseCultivation;

/// <summary>One opening of the secret realm: which opening (<see cref="Index"/>), where, and
/// its day window. <see cref="CloseDay"/> is EXCLUSIVE.</summary>
public readonly record struct SecretRealmInfo(long Index, int X, int Y, long OpenDay, long CloseDay, string Name);

/// <summary>
/// The secret-realm schedule — pure functions over (config, map, day), consulted by the
/// runner (trial + rumor), the UI (marker + entry panel), and the tests alike. Everything
/// derives deterministically from the world's generation seed and the absolute month index:
/// no dynamic state, so saves need only remember what the PLAYER did (rumor heard, trial
/// spent), never where/when realms open.
/// </summary>
public static class SecretRealms
{
    /// <summary>The realm open (or the one whose window contains <paramref name="day"/>),
    /// or null between openings. Opening k starts at month FirstOpenMonth + k·PeriodMonths
    /// and lasts OpenMonths.</summary>
    public static SecretRealmInfo? TryGetCurrent(CultivationConfig config, WorldMap map, long day)
    {
        var cfg = config.SecretRealm;
        var month = day / config.Time.DaysPerMonth;
        if (month < cfg.FirstOpenMonth)
        {
            return null;
        }
        var index = (month - cfg.FirstOpenMonth) / cfg.PeriodMonths;
        var startMonth = cfg.FirstOpenMonth + index * cfg.PeriodMonths;
        if (month >= startMonth + cfg.OpenMonths)
        {
            return null; // this period's window has already closed
        }
        var (x, y) = LocationOf(config, map, index);
        return new SecretRealmInfo(
            index, x, y,
            startMonth * config.Time.DaysPerMonth,
            (startMonth + cfg.OpenMonths) * config.Time.DaysPerMonth,
            NameOf(config, index));
    }

    /// <summary>True when month <paramref name="monthIndex"/> is the FIRST month of an
    /// opening — the runner announces it in the chronicle on that crossing.</summary>
    public static bool OpensAtMonth(CultivationConfig config, long monthIndex)
    {
        var cfg = config.SecretRealm;
        return monthIndex >= cfg.FirstOpenMonth &&
               (monthIndex - cfg.FirstOpenMonth) % cfg.PeriodMonths == 0;
    }

    /// <summary>Deterministic location of opening <paramref name="index"/>: a walkable,
    /// site-free tile drawn from an index-seeded stream (bounded, with a scan fallback so a
    /// pathological map still terminates).</summary>
    public static (int X, int Y) LocationOf(CultivationConfig config, WorldMap map, long index)
    {
        var rng = new Pcg32(unchecked(map.GenerationSeed * 31 + (int)index * 131), 97);
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var x = rng.Next(map.Width);
            var y = rng.Next(map.Height);
            ref readonly var tile = ref map.TileAt(x, y);
            if (tile.SiteIndex < 0 && Pathfinding.IsWalkable(config, in tile))
            {
                return (x, y);
            }
        }
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                ref readonly var tile = ref map.TileAt(x, y);
                if (tile.SiteIndex < 0 && Pathfinding.IsWalkable(config, in tile))
                {
                    return (x, y);
                }
            }
        }
        return (map.Width / 2, map.Height / 2); // unreachable on any generated map
    }

    /// <summary>Name of opening <paramref name="index"/> — hashed through the authored pool
    /// so consecutive openings don't just cycle in order.</summary>
    public static string NameOf(CultivationConfig config, long index)
    {
        var pool = config.Names.RealmNames;
        return pool[(int)((ulong)(index * 2654435761L + 97) % (ulong)pool.Length)];
    }

    /// <summary>Octant name for the rumor's rough bearing (x east, y south), from the
    /// authored 8-entry direction table: 东 东南 南 西南 西 西北 北 东北.</summary>
    public static string DirectionName(CultivationConfig config, int dx, int dy)
    {
        var octant = (int)Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4.0));
        return config.Text.Ui.DirectionNames[(octant + 8) % 8];
    }
}
