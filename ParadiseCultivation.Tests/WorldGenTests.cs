namespace ParadiseCultivation.Tests;

/// <summary>World generation: deterministic per seed, diverse terrain, and the design doc's
/// Phase-1 checklist (at least two terrain types, at least one town and one sect).</summary>
public class WorldGenTests
{
    [Test]
    public async Task same_seed_generates_the_identical_world()
    {
        var a = WorldGenerator.Generate(Fixture.Config, seed: 42, sizeIndex: 0);
        var b = WorldGenerator.Generate(Fixture.Config, seed: 42, sizeIndex: 0);

        await Assert.That(b.Map.Width).IsEqualTo(a.Map.Width);
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
        var a = WorldGenerator.Generate(Fixture.Config, seed: 1, sizeIndex: 0);
        var b = WorldGenerator.Generate(Fixture.Config, seed: 2, sizeIndex: 0);

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
    public async Task worlds_have_diverse_terrain_and_required_sites(int seed)
    {
        var (map, _) = WorldGenerator.Generate(Fixture.Config, seed, sizeIndex: 0);

        var kinds = map.Tiles.Select(t => t.Terrain).Distinct().Count();
        await Assert.That(kinds).IsGreaterThanOrEqualTo(2);
        await Assert.That(map.Sites.Count(s => s.Kind == SiteKind.Town)).IsGreaterThanOrEqualTo(1);
        await Assert.That(map.Sites.Count(s => s.Kind == SiteKind.Sect)).IsGreaterThanOrEqualTo(1);
        await Assert.That(map.Tiles.Count(t => t.Terrain == Terrain.SpiritVein)).IsGreaterThan(0);
    }

    [Test]
    public async Task sites_sit_on_walkable_tiles_and_carry_their_population()
    {
        var (map, npcs) = WorldGenerator.Generate(Fixture.Config, seed: 77, sizeIndex: 0);

        for (var index = 0; index < map.Sites.Count; index++)
        {
            var site = map.Sites[index];
            var terrain = map.TileAt(site.X, site.Y).Terrain;
            await Assert.That(terrain is Terrain.Mountain or Terrain.River).IsFalse();
            await Assert.That(map.TileAt(site.X, site.Y).SiteIndex).IsEqualTo((short)index);

            var expected = site.Kind == SiteKind.Town
                ? Fixture.Config.World.NpcsPerTown
                : Fixture.Config.World.NpcsPerSect;
            await Assert.That(npcs.Count(n => n.SiteIndex == index)).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task sect_leaders_outrank_their_disciples_floor()
    {
        var (map, npcs) = WorldGenerator.Generate(Fixture.Config, seed: 5, sizeIndex: 0);
        var leaders = npcs.Where(n => n.IsLeader).ToList();

        await Assert.That(leaders.Count).IsEqualTo(map.Sites.Count(s => s.Kind == SiteKind.Sect));
        foreach (var leader in leaders)
        {
            await Assert.That(leader.RealmIndex).IsGreaterThanOrEqualTo(Fixture.Config.Npc.LeaderMinRealmIndex);
        }
    }

    [Test]
    public async Task vein_quality_only_appears_on_spirit_vein_tiles()
    {
        var (map, _) = WorldGenerator.Generate(Fixture.Config, seed: 99, sizeIndex: 0);
        foreach (var tile in map.Tiles)
        {
            if (tile.Terrain == Terrain.SpiritVein)
            {
                await Assert.That((int)tile.VeinQuality).IsGreaterThanOrEqualTo(1);
                await Assert.That((int)tile.VeinQuality).IsLessThanOrEqualTo(4);
            }
            else
            {
                await Assert.That((int)tile.VeinQuality).IsEqualTo(0);
            }
        }
    }
}
