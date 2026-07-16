namespace ParadiseCultivation;

public enum GamePhase
{
    /// <summary>World preview / reroll screen — the journey has not begun.</summary>
    NewGame,
    Playing,
    Dead,
}

public enum Terrain : byte
{
    Plains,
    Forest,
    River,
    Mountain,
    SpiritVein,
}

public struct Tile
{
    public Terrain Terrain;
    /// <summary>1…4 when <see cref="Terrain"/> is SpiritVein, else 0.</summary>
    public byte VeinQuality;
    /// <summary>Index into <see cref="WorldMap.Sites"/>, or -1.</summary>
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
    public required int SizeIndex { get; init; }
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
