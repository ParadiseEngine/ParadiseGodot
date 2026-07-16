using System.Diagnostics;

namespace ParadiseCultivation.Tests;

/// <summary>World generation under the locked direction: deterministic per seed, the 8 base
/// terrains, spirit veins as an L3 layer, validated worlds (full site counts, one walkable
/// landmass) with derived-seed rerolls, and both locked presets — the 32x32 Demo (1 town,
/// 1 sect, 6 NPCs) and the 256x256 formal world (20 towns, 8 sects, 400 NPCs, ≤60 s).</summary>
public class WorldGenTests
{
    [Test]
    public async Task same_seed_generates_the_identical_world()
    {
        var a = WorldGenerator.Generate(Fixture.Config, seed: 42, presetIndex: 0);
        var b = WorldGenerator.Generate(Fixture.Config, seed: 42, presetIndex: 0);

        await Assert.That(b.Map.Width).IsEqualTo(a.Map.Width);
        await Assert.That(b.Map.GenerationSeed).IsEqualTo(a.Map.GenerationSeed);
        var differingTiles = 0;
        for (var i = 0; i < a.Map.Tiles.Length; i++)
        {
            if (a.Map.Tiles[i].Terrain != b.Map.Tiles[i].Terrain ||
                a.Map.Tiles[i].VeinQuality != b.Map.Tiles[i].VeinQuality)
            {
                differingTiles++;
            }
        }
        await Assert.That(differingTiles).IsEqualTo(0);
        await Assert.That(b.Map.Sites.Select(s => (s.Kind, s.Name, s.X, s.Y)))
            .IsEquivalentTo(a.Map.Sites.Select(s => (s.Kind, s.Name, s.X, s.Y)));
        await Assert.That(b.Npcs).IsEquivalentTo(a.Npcs); // NpcSpec is a value record
    }

    [Test]
    public async Task different_seeds_generate_different_worlds()
    {
        var a = WorldGenerator.Generate(Fixture.Config, seed: 1, presetIndex: 0);
        var b = WorldGenerator.Generate(Fixture.Config, seed: 2, presetIndex: 0);

        var differing = 0;
        for (var i = 0; i < a.Map.Tiles.Length; i++)
        {
            if (a.Map.Tiles[i].Terrain != b.Map.Tiles[i].Terrain) differing++;
        }
        await Assert.That(differing).IsGreaterThan(a.Map.Tiles.Length / 20);
    }

    [Test]
    [Arguments(7)]
    [Arguments(123456)]
    [Arguments(-9)]
    public async Task demo_worlds_validate_with_full_site_counts_and_reachability(int seed)
    {
        var (map, npcs) = WorldGenerator.Generate(Fixture.Config, seed, presetIndex: 0);
        var preset = Fixture.Config.World.Presets[0];

        await Assert.That(map.Sites.Count(s => s.Kind == SiteKind.Town)).IsEqualTo(preset.TownCount);
        await Assert.That(map.Sites.Count(s => s.Kind == SiteKind.Sect)).IsEqualTo(preset.SectCount);
        // The Demo boundary: 1 town + 1 sect, 6 NPCs.
        await Assert.That(npcs.Count).IsEqualTo(6);

        var kinds = map.Tiles.Select(t => t.Terrain).Distinct().Count();
        await Assert.That(kinds).IsGreaterThanOrEqualTo(2);
        await Assert.That(map.Tiles.Count(t => t.VeinQuality > 0)).IsGreaterThan(0);

        // Validation contract: every site reachable on foot from the first town.
        var home = map.Sites[0];
        var region = Pathfinding.WalkableRegion(Fixture.Config, map, home.X, home.Y);
        foreach (var site in map.Sites)
        {
            await Assert.That(region[site.Y * map.Width + site.X]).IsTrue();
        }
    }

    [Test]
    public async Task formal_world_generates_locked_content_within_the_time_budget()
    {
        var stopwatch = Stopwatch.StartNew();
        var (map, npcs) = WorldGenerator.Generate(Fixture.Config, seed: 20260716, presetIndex: 1);
        stopwatch.Stop();

        // Locked: 256x256, 20 towns, 8 sects, 400 initially active NPCs, within 60 seconds.
        await Assert.That(map.Width).IsEqualTo(256);
        await Assert.That(map.Sites.Count(s => s.Kind == SiteKind.Town)).IsEqualTo(20);
        await Assert.That(map.Sites.Count(s => s.Kind == SiteKind.Sect)).IsEqualTo(8);
        await Assert.That(npcs.Count).IsEqualTo(400);
        await Assert.That(stopwatch.Elapsed.TotalSeconds).IsLessThan(60.0);

        var kinds = map.Tiles.Select(t => t.Terrain).Distinct().Count();
        await Assert.That(kinds).IsGreaterThanOrEqualTo(4); // a real biome spread at scale
    }

    [Test]
    public async Task terrain_values_stay_inside_the_eight_locked_types()
    {
        var (map, _) = WorldGenerator.Generate(Fixture.Config, seed: 77, presetIndex: 0);
        foreach (var tile in map.Tiles)
        {
            await Assert.That((int)tile.Terrain).IsGreaterThanOrEqualTo(0);
            await Assert.That((int)tile.Terrain).IsLessThanOrEqualTo((int)Terrain.Swamp);
            if (tile.Terrain == Terrain.Water)
            {
                await Assert.That((int)tile.VeinQuality).IsEqualTo(0); // veins only on dry land
            }
            await Assert.That((int)tile.VeinQuality).IsLessThanOrEqualTo(4);
        }
    }

    [Test]
    public async Task sites_sit_on_walkable_tiles_and_carry_their_population()
    {
        var (map, npcs) = WorldGenerator.Generate(Fixture.Config, seed: 77, presetIndex: 0);
        var preset = Fixture.Config.World.Presets[0];

        for (var index = 0; index < map.Sites.Count; index++)
        {
            var site = map.Sites[index];
            await Assert.That(Pathfinding.IsWalkable(Fixture.Config, map.TileAt(site.X, site.Y))).IsTrue();
            await Assert.That(map.TileAt(site.X, site.Y).SiteIndex).IsEqualTo((short)index);

            var expected = site.Kind == SiteKind.Town ? preset.NpcsPerTown : preset.NpcsPerSect;
            await Assert.That(npcs.Count(n => n.SiteIndex == index)).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task sect_leaders_outrank_their_disciples_floor()
    {
        var (map, npcs) = WorldGenerator.Generate(Fixture.Config, seed: 5, presetIndex: 0);
        var leaders = npcs.Where(n => n.IsLeader).ToList();

        await Assert.That(leaders.Count).IsEqualTo(map.Sites.Count(s => s.Kind == SiteKind.Sect));
        foreach (var leader in leaders)
        {
            await Assert.That(leader.RealmIndex).IsGreaterThanOrEqualTo(Fixture.Config.Npc.LeaderMinRealmIndex);
        }
    }
}
