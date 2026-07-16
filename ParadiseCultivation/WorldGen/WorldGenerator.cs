namespace ParadiseCultivation;

/// <summary>The generator's output: the immutable map plus the initial cultivator population
/// (as spawn specs — the runner turns them into ECS entities).</summary>
public sealed record GeneratedWorld(WorldMap Map, IReadOnlyList<NpcSpec> Npcs);

/// <summary>Procedural world generation: layered value noise decides terrain (mountains,
/// rivers, forests, plains) and spirit-vein placement/quality; towns and sects are then
/// placed greedily on the best-scoring tiles with minimum separation, and each site is
/// populated with cultivator specs. Deterministic per (seed, sizeIndex). ECS-free — the
/// runner owns spawning.</summary>
public static class WorldGenerator
{
    public static GeneratedWorld Generate(CultivationConfig config, int seed, int sizeIndex)
    {
        var size = config.World.Sizes[sizeIndex];
        var terrain = config.World.Terrain;
        var tiles = new Tile[size.Width * size.Height];

        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                var elevation = ValueNoise.Fbm(seed, x * terrain.ElevationScale, y * terrain.ElevationScale, terrain.ElevationOctaves);
                var moisture = ValueNoise.Fbm(seed + 7919, x * terrain.MoistureScale, y * terrain.MoistureScale, terrain.ElevationOctaves);
                var spirit = ValueNoise.Fbm(seed + 15731, x * terrain.SpiritScale, y * terrain.SpiritScale, terrain.ElevationOctaves);

                ref var tile = ref tiles[y * size.Width + x];
                tile.SiteIndex = -1;

                if (elevation > terrain.MountainThreshold) tile.Terrain = Terrain.Mountain;
                else if (elevation < terrain.WaterThreshold) tile.Terrain = Terrain.River;
                else if (moisture > terrain.ForestThreshold) tile.Terrain = Terrain.Forest;
                else tile.Terrain = Terrain.Plains;

                // Spirit veins overlay any dry land; quality = highest threshold passed.
                if (tile.Terrain != Terrain.River)
                {
                    byte quality = 0;
                    var thresholds = terrain.VeinQualityThresholds;
                    for (var q = 0; q < thresholds.Length; q++)
                    {
                        if (spirit >= thresholds[q]) quality = (byte)(q + 1);
                    }
                    if (quality > 0)
                    {
                        tile.Terrain = Terrain.SpiritVein;
                        tile.VeinQuality = quality;
                    }
                }
            }
        }

        var sites = new List<Site>();
        var rng = new Random(seed ^ 0x5EC7C0DE);
        PlaceKind(tiles, sites, size, SiteKind.Town, size.TownCount, config.World.MinTownSeparation, TownScore,
            () => SiteName(config.Names.TownPrefixes, config.Names.TownSuffixes, rng));
        PlaceKind(tiles, sites, size, SiteKind.Sect, size.SectCount, config.World.MinSectSeparation, SectScore,
            () => SiteName(config.Names.SectPrefixes, config.Names.SectSuffixes, rng));

        var map = new WorldMap
        {
            Seed = seed,
            SizeIndex = sizeIndex,
            Width = size.Width,
            Height = size.Height,
            Tiles = tiles,
            Sites = sites,
        };

        var npcs = new List<NpcSpec>();
        for (var siteIndex = 0; siteIndex < sites.Count; siteIndex++)
        {
            var count = sites[siteIndex].Kind == SiteKind.Town ? config.World.NpcsPerTown : config.World.NpcsPerSect;
            for (var i = 0; i < count; i++)
            {
                var isLeader = sites[siteIndex].Kind == SiteKind.Sect && i == 0;
                npcs.Add(CreateNpcSpec(config, rng, npcs.Count + 1, map, siteIndex, isLeader));
            }
        }

        return new GeneratedWorld(map, npcs);
    }

    /// <summary>Also used by the runner's post-pass to generate replacement cultivators.</summary>
    public static NpcSpec CreateNpcSpec(
        CultivationConfig config, Random rng, int npcId, WorldMap map, int siteIndex, bool isLeader, int? ageYears = null)
    {
        var npcCfg = config.Npc;
        var site = map.Sites[siteIndex];
        var maxRealm = site.Kind == SiteKind.Town ? npcCfg.TownMaxRealmIndex : npcCfg.SectMaxRealmIndex;
        var minRealm = isLeader ? npcCfg.LeaderMinRealmIndex : 0;
        var realm = Math.Clamp(minRealm + rng.Next(maxRealm - minRealm + 1), 0, config.Realms.Length - 1);

        var lifespan = config.Realms[realm].LifespanYears;
        var age = ageYears ?? (npcCfg.ReplacementAgeYears + rng.Next(Math.Max(1, lifespan / 2 - npcCfg.ReplacementAgeYears)));
        var daysPerYear = (long)config.Time.DaysPerMonth * config.Time.MonthsPerYear;

        return new NpcSpec(
            NpcId: npcId,
            SiteIndex: siteIndex,
            IsLeader: isLeader,
            RealmIndex: realm,
            SubStage: rng.Next(config.SubStages.Length),
            AgeDays: (double)age * daysPerYear,
            SurnameIndex: rng.Next(config.Names.Surnames.Length),
            GivenNameIndex: rng.Next(config.Names.GivenNames.Length),
            PersonalityIndex: rng.Next(npcCfg.Personalities.Length),
            CharmTier: RollWeighted(rng, config.CharmTiers, static tier => tier.Weight));
    }

    internal static int RollWeighted<T>(Random rng, T[] items, Func<T, int> weight)
    {
        var total = 0;
        foreach (var item in items) total += weight(item);
        var roll = rng.Next(total);
        for (var i = 0; i < items.Length; i++)
        {
            roll -= weight(items[i]);
            if (roll < 0) return i;
        }
        return items.Length - 1;
    }

    private static string SiteName(string[] prefixes, string[] suffixes, Random rng) =>
        $"{prefixes[rng.Next(prefixes.Length)]}{suffixes[rng.Next(suffixes.Length)]}";

    private static void PlaceKind(
        Tile[] tiles, List<Site> sites, WorldSizeConfig size, SiteKind kind, int count, int minSeparation,
        Func<Tile[], WorldSizeConfig, int, int, float> score, Func<string> nameFor)
    {
        // Greedy: repeatedly take the best-scoring free tile far enough from earlier sites.
        for (var placed = 0; placed < count; placed++)
        {
            var bestScore = float.MinValue;
            var bestX = -1;
            var bestY = -1;
            for (var y = 1; y < size.Height - 1; y++)
            {
                for (var x = 1; x < size.Width - 1; x++)
                {
                    ref var tile = ref tiles[y * size.Width + x];
                    if (tile.SiteIndex >= 0 || tile.Terrain is Terrain.Mountain or Terrain.River) continue;
                    if (!FarFromSites(sites, x, y, minSeparation)) continue;
                    var s = score(tiles, size, x, y);
                    if (s > bestScore)
                    {
                        bestScore = s;
                        bestX = x;
                        bestY = y;
                    }
                }
            }

            if (bestX < 0) break; // map too small/crowded for the remaining sites

            sites.Add(new Site { Kind = kind, Name = nameFor(), X = bestX, Y = bestY });
            tiles[bestY * size.Width + bestX].SiteIndex = (short)(sites.Count - 1);
        }
    }

    private static bool FarFromSites(List<Site> sites, int x, int y, int minSeparation)
    {
        foreach (var site in sites)
        {
            if (Math.Max(Math.Abs(site.X - x), Math.Abs(site.Y - y)) < minSeparation) return false;
        }
        return true;
    }

    /// <summary>Towns like plains beside water: flat ground, river adjacency bonus.</summary>
    private static float TownScore(Tile[] tiles, WorldSizeConfig size, int x, int y)
    {
        var score = tiles[y * size.Width + x].Terrain == Terrain.Plains ? 2f : 0.5f;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx >= 0 && nx < size.Width && ny >= 0 && ny < size.Height &&
                    tiles[ny * size.Width + nx].Terrain == Terrain.River)
                {
                    score += 1f;
                }
            }
        }
        return score;
    }

    /// <summary>Sects settle on/near spirit veins — score sums nearby vein quality.</summary>
    private static float SectScore(Tile[] tiles, WorldSizeConfig size, int x, int y)
    {
        var score = 0f;
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || nx >= size.Width || ny < 0 || ny >= size.Height) continue;
                ref var tile = ref tiles[ny * size.Width + nx];
                if (tile.Terrain == Terrain.SpiritVein) score += tile.VeinQuality;
            }
        }
        return score;
    }
}
