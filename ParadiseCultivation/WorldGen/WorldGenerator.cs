namespace ParadiseCultivation;

/// <summary>The generator's output: the immutable map plus the initial cultivator population
/// (as spawn specs — the runner turns them into ECS entities).</summary>
public sealed record GeneratedWorld(WorldMap Map, IReadOnlyList<NpcSpec> Npcs);

/// <summary>Procedural world generation per the locked direction: three value-noise channels
/// (elevation / moisture / temperature) map to the 8 base terrains; spirit veins are a
/// separate L3 layer over any dry tile; towns and sects place greedily on the best-scoring
/// walkable tiles with per-preset separation. A generated world must VALIDATE (full site
/// counts, all sites foot-reachable from the first town) or the whole world rerolls with a
/// deterministically derived seed — same requested seed, same final world.</summary>
public static class WorldGenerator
{
    public static GeneratedWorld Generate(CultivationConfig config, int seed, int presetIndex)
    {
        var attempts = Math.Max(1, config.World.MaxGenerationAttempts);
        var generationSeed = seed;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var candidate = GenerateOnce(config, seed, generationSeed, presetIndex);
            if (candidate is not null && Validate(config, candidate.Map))
            {
                return candidate;
            }
            generationSeed = unchecked(generationSeed * -1640531527 + 13); // derived reroll
        }
        throw new InvalidDataException(
            $"world generation failed validation {attempts} times for seed {seed}, preset {presetIndex} — " +
            "terrain thresholds and site counts are incompatible.");
    }

    private static GeneratedWorld? GenerateOnce(CultivationConfig config, int requestedSeed, int seed, int presetIndex)
    {
        var preset = config.World.Presets[presetIndex];
        var terrain = config.World.Terrain;
        var tiles = new Tile[preset.Width * preset.Height];

        for (var y = 0; y < preset.Height; y++)
        {
            for (var x = 0; x < preset.Width; x++)
            {
                var elevation = ValueNoise.Fbm(seed, x * terrain.ElevationScale, y * terrain.ElevationScale, terrain.ElevationOctaves);
                var moisture = ValueNoise.Fbm(seed + 7919, x * terrain.MoistureScale, y * terrain.MoistureScale, terrain.ElevationOctaves);
                var temperature = ValueNoise.Fbm(seed + 104729, x * terrain.TemperatureScale, y * terrain.TemperatureScale, terrain.ElevationOctaves);
                var spirit = ValueNoise.Fbm(seed + 15731, x * terrain.SpiritScale, y * terrain.SpiritScale, terrain.ElevationOctaves);

                ref var tile = ref tiles[y * preset.Width + x];
                tile.SiteIndex = -1;
                tile.Terrain = Classify(terrain, elevation, moisture, temperature);

                // L3 spiritual energy layers over any dry land, independent of terrain.
                if (tile.Terrain != Terrain.Water)
                {
                    byte quality = 0;
                    for (var q = 0; q < terrain.VeinQualityThresholds.Length; q++)
                    {
                        if (spirit >= terrain.VeinQualityThresholds[q]) quality = (byte)(q + 1);
                    }
                    tile.VeinQuality = quality;
                }
            }
        }

        // Sites are confined to the LARGEST connected walkable region: the town score's
        // lakeside bonus would otherwise happily settle unreachable lake islands, and there
        // is no sea travel to bridge them (reachability is a validation requirement).
        var mainland = LargestWalkableRegion(config, tiles, preset);

        var sites = new List<Site>();
        var rng = new SystemRng(seed ^ 0x5EC7C0DE);
        PlaceKind(config, tiles, sites, preset, mainland, SiteKind.Town, preset.TownCount, preset.MinTownSeparation, TownScore,
            () => SiteName(config.Names.TownPrefixes, config.Names.TownSuffixes, rng));
        PlaceKind(config, tiles, sites, preset, mainland, SiteKind.Sect, preset.SectCount, preset.MinSectSeparation, SectScore,
            () => SiteName(config.Names.SectPrefixes, config.Names.SectSuffixes, rng));

        var map = new WorldMap
        {
            Seed = requestedSeed,
            GenerationSeed = seed,
            PresetIndex = presetIndex,
            Width = preset.Width,
            Height = preset.Height,
            Tiles = tiles,
            Sites = sites,
        };

        var npcs = new List<NpcSpec>();
        for (var siteIndex = 0; siteIndex < sites.Count; siteIndex++)
        {
            var count = sites[siteIndex].Kind == SiteKind.Town ? preset.NpcsPerTown : preset.NpcsPerSect;
            for (var i = 0; i < count; i++)
            {
                var isLeader = sites[siteIndex].Kind == SiteKind.Sect && i == 0;
                npcs.Add(CreateNpcSpec(config, rng, npcs.Count + 1, map, siteIndex, isLeader));
            }
        }

        return new GeneratedWorld(map, npcs);
    }

    private static Terrain Classify(TerrainConfig terrain, float elevation, float moisture, float temperature)
    {
        if (elevation < terrain.WaterThreshold) return Terrain.Water;
        if (elevation > terrain.MountainThreshold) return Terrain.Mountains;
        if (temperature < terrain.SnowTemperature) return Terrain.Snowfield;
        if (elevation > terrain.HillThreshold) return Terrain.Hills;
        if (moisture > terrain.SwampMoisture && elevation < terrain.WaterThreshold + terrain.SwampElevationMargin)
        {
            return Terrain.Swamp;
        }
        if (moisture < terrain.DesertMoisture && temperature > terrain.DesertTemperature) return Terrain.Desert;
        if (moisture > terrain.ForestMoisture) return Terrain.Forest;
        return Terrain.Plains;
    }

    /// <summary>Locked generation principle: a world only ships when every requested site
    /// placed AND every site is foot-reachable from the first town (one landmass — there is
    /// no sea travel to bridge islands).</summary>
    private static bool Validate(CultivationConfig config, WorldMap map)
    {
        var preset = config.World.Presets[map.PresetIndex];
        var towns = 0;
        var sects = 0;
        foreach (var site in map.Sites)
        {
            if (site.Kind == SiteKind.Town) towns++;
            else sects++;
        }
        if (towns != preset.TownCount || sects != preset.SectCount) return false;

        // The demo needs spirit veins for cultivation-spot gameplay; tiny maps can roll none.
        var hasVein = false;
        foreach (var tile in map.Tiles)
        {
            if (tile.VeinQuality > 0) { hasVein = true; break; }
        }
        if (!hasVein) return false;

        var home = map.Sites[0];
        var reachable = Pathfinding.WalkableRegion(config, map, home.X, home.Y);
        foreach (var site in map.Sites)
        {
            if (!reachable[site.Y * map.Width + site.X]) return false;
        }
        return true;
    }

    /// <summary>Also used by the runner's post-pass to generate replacement cultivators.</summary>
    public static NpcSpec CreateNpcSpec(
        CultivationConfig config, IRng rng, int npcId, WorldMap map, int siteIndex, bool isLeader, int? ageYears = null)
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

    internal static int RollWeighted<T>(IRng rng, T[] items, Func<T, int> weight)
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

    private static string SiteName(string[] prefixes, string[] suffixes, IRng rng) =>
        $"{prefixes[rng.Next(prefixes.Length)]}{suffixes[rng.Next(suffixes.Length)]}";

    /// <summary>Flood-fill every walkable component; true where a tile belongs to the biggest.</summary>
    private static bool[] LargestWalkableRegion(CultivationConfig config, Tile[] tiles, WorldPresetConfig preset)
    {
        var size = preset.Width * preset.Height;
        var component = new int[size];
        Array.Fill(component, -1);
        var componentSizes = new List<int>();
        var queue = new Queue<int>();
        for (var start = 0; start < size; start++)
        {
            if (component[start] >= 0 ||
                config.World.Terrain.MoveCostDays[(int)tiles[start].Terrain] <= 0f)
            {
                continue;
            }
            var id = componentSizes.Count;
            var count = 0;
            component[start] = id;
            queue.Enqueue(start);
            while (queue.TryDequeue(out var current))
            {
                count++;
                var cx = current % preset.Width;
                var cy = current / preset.Width;
                Visit(cx + 1, cy);
                Visit(cx - 1, cy);
                Visit(cx, cy + 1);
                Visit(cx, cy - 1);

                void Visit(int nx, int ny)
                {
                    if (nx < 0 || nx >= preset.Width || ny < 0 || ny >= preset.Height) return;
                    var next = ny * preset.Width + nx;
                    if (component[next] >= 0 ||
                        config.World.Terrain.MoveCostDays[(int)tiles[next].Terrain] <= 0f)
                    {
                        return;
                    }
                    component[next] = id;
                    queue.Enqueue(next);
                }
            }
            componentSizes.Add(count);
        }

        var largest = 0;
        for (var i = 1; i < componentSizes.Count; i++)
        {
            if (componentSizes[i] > componentSizes[largest]) largest = i;
        }
        var mask = new bool[size];
        for (var i = 0; i < size; i++)
        {
            mask[i] = component[i] == largest;
        }
        return mask;
    }

    private static void PlaceKind(
        CultivationConfig config, Tile[] tiles, List<Site> sites, WorldPresetConfig preset, bool[] mainland,
        SiteKind kind, int count, int minSeparation,
        Func<CultivationConfig, Tile[], WorldPresetConfig, int, int, float> score, Func<string> nameFor)
    {
        // Greedy: repeatedly take the best-scoring mainland free tile far enough from earlier sites.
        for (var placed = 0; placed < count; placed++)
        {
            var bestScore = float.MinValue;
            var bestX = -1;
            var bestY = -1;
            for (var y = 1; y < preset.Height - 1; y++)
            {
                for (var x = 1; x < preset.Width - 1; x++)
                {
                    ref var tile = ref tiles[y * preset.Width + x];
                    if (tile.SiteIndex >= 0) continue;
                    if (!mainland[y * preset.Width + x]) continue; // walkable + reachable
                    if (tile.Terrain == Terrain.Mountains) continue; // settlements avoid peaks
                    if (!FarFromSites(sites, x, y, minSeparation)) continue;
                    var s = score(config, tiles, preset, x, y);
                    if (s > bestScore)
                    {
                        bestScore = s;
                        bestX = x;
                        bestY = y;
                    }
                }
            }

            if (bestX < 0) break; // map too small/crowded — validation will reroll the world

            sites.Add(new Site { Kind = kind, Name = nameFor(), X = bestX, Y = bestY });
            tiles[bestY * preset.Width + bestX].SiteIndex = (short)(sites.Count - 1);
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

    /// <summary>Towns like open lowland: plains best, forest/hills acceptable, lakeside bonus.</summary>
    private static float TownScore(CultivationConfig config, Tile[] tiles, WorldPresetConfig preset, int x, int y)
    {
        var tile = tiles[y * preset.Width + x];
        var score = tile.Terrain switch
        {
            Terrain.Plains => 3f,
            Terrain.Forest => 1.5f,
            Terrain.Hills => 1f,
            _ => 0.25f,
        };
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx >= 0 && nx < preset.Width && ny >= 0 && ny < preset.Height &&
                    tiles[ny * preset.Width + nx].Terrain == Terrain.Water)
                {
                    score += 1f; // lakeside
                }
            }
        }
        return score;
    }

    /// <summary>Sects settle on/near spirit veins — score sums the nearby L3 layer.</summary>
    private static float SectScore(CultivationConfig config, Tile[] tiles, WorldPresetConfig preset, int x, int y)
    {
        var score = 0f;
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || nx >= preset.Width || ny < 0 || ny >= preset.Height) continue;
                score += tiles[ny * preset.Width + nx].VeinQuality;
            }
        }
        return score;
    }
}
