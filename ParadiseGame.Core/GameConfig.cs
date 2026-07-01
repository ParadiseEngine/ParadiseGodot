namespace ParadiseGame.Core;

/// <summary>
/// ECS configuration for the shared runtime simulation. <c>[DefaultConfig]</c> makes the source
/// generator emit the <c>World</c> / <c>SharedWorld</c> / <c>SharedWorldFactory</c> aliases used by
/// <see cref="Simulation"/>. Mirrors Paradise.ECS.Sample's GameConfig.
/// </summary>
[DefaultConfig]
public readonly struct GameConfig : IConfig
{
    public GameConfig() { }

    public static int ChunkSize => 16 * 1024;
    public static int MaxMetaBlocks => 1024;
    public static int EntityIdByteSize => sizeof(int);

    public int DefaultEntityCapacity { get; init; } = 1024;
    public int DefaultChunkCapacity { get; init; } = 256;
    public IAllocator ChunkAllocator { get; init; } = NativeMemoryAllocator.Shared;
    public IAllocator MetadataAllocator { get; init; } = NativeMemoryAllocator.Shared;
    public IAllocator LayoutAllocator { get; init; } = NativeMemoryAllocator.Shared;
}
