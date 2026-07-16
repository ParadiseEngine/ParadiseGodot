namespace ParadiseCultivation;

public enum GamePhase
{
    /// <summary>World preview / reroll screen — the journey has not begun.</summary>
    NewGame,
    Playing,
    Dead,
}

/// <summary>The eight locked base terrains (high-concept v2.0 §8.1). Rivers, roads, and sea
/// were removed from the design; water is inland lakes only, impassable on foot.</summary>
public enum Terrain : byte
{
    Plains,
    Forest,
    Hills,
    Mountains,
    Water,
    Desert,
    Snowfield,
    Swamp,
}

/// <summary>One logical tile, carrying the map data layers the slice models so far:
/// L0/L1 landform+ecology collapsed into <see cref="Terrain"/> (8 base types), L3 spiritual
/// energy as <see cref="VeinQuality"/> (a LAYER now, not a terrain type — any dry tile can
/// carry a vein), L4 locations as <see cref="SiteIndex"/>. L2 linear features were removed
/// by the design (no roads/rivers); L5 dynamic overlays are runtime-only.</summary>
public struct Tile
{
    public Terrain Terrain;
    /// <summary>L3 spiritual energy: 0 = none, 1…4 = vein quality.</summary>
    public byte VeinQuality;
    /// <summary>L4 locations: index into <see cref="WorldMap.Sites"/>, or -1.</summary>
    public short SiteIndex;
}

public enum SiteKind : byte
{
    Town,
    Sect,
}

public sealed class Site
{
    public required SiteKind Kind { get; init; }
    public required string Name { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
}

/// <summary>
/// The immutable generated map — terrain tiles and town/sect sites. Deliberately OUTSIDE the
/// ECS (the navmesh/CollisionWorld precedent: static world data is not simulation state):
/// never mutated after generation, so it is safe to read from any thread alongside published
/// world snapshots. Dynamic state (the cultivators) lives in ECS components.
/// </summary>
public sealed class WorldMap
{
    public required int Seed { get; init; }
    /// <summary>The seed that actually produced this world — differs from <see cref="Seed"/>
    /// when validation rerolled (derived deterministically, so reproducibility holds).</summary>
    public required int GenerationSeed { get; init; }
    public required int PresetIndex { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required Tile[] Tiles { get; init; }
    public required IReadOnlyList<Site> Sites { get; init; }

    public ref readonly Tile TileAt(int x, int y) => ref Tiles[y * Width + x];

    public bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;
}

/// <summary>A generated cultivator, ready to be spawned as an ECS entity — the seam between
/// the (ECS-free) generator and the runner's spawn path.</summary>
public readonly record struct NpcSpec(
    int NpcId,
    int SiteIndex,
    bool IsLeader,
    int RealmIndex,
    int SubStage,
    double AgeDays,
    int SurnameIndex,
    int GivenNameIndex,
    int PersonalityIndex,
    int CharmTier);

/// <summary>One dated log line — NPC memories and the world chronicle.</summary>
public readonly record struct MemoryEntry(long Day, string Summary);
