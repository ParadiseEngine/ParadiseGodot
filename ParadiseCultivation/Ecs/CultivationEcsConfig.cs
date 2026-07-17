namespace ParadiseCultivation;

/// <summary>
/// ECS configuration for the cultivation simulation. <c>[DefaultConfig]</c> makes the source
/// generator emit this assembly's <c>World</c> / <c>SharedWorld</c> / <c>SharedWorldFactory</c>
/// aliases (distinct from ParadiseGame's — each assembly gets its own world type). Mirrors
/// ParadiseGame's GameConfig.
/// </summary>
[DefaultConfig]
public readonly struct CultivationEcsConfig : IConfig
{
    public CultivationEcsConfig() { }

    public static int ChunkSize => 16 * 1024;
    public static int MaxMetaBlocks => 1024;
    public static int EntityIdByteSize => sizeof(int);

    public int DefaultEntityCapacity { get; init; } = 1024;
    public int DefaultChunkCapacity { get; init; } = 256;
    public IAllocator ChunkAllocator { get; init; } = NativeMemoryAllocator.Shared;
    public IAllocator MetadataAllocator { get; init; } = NativeMemoryAllocator.Shared;
    public IAllocator LayoutAllocator { get; init; } = NativeMemoryAllocator.Shared;
}
