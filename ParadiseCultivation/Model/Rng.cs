namespace ParadiseCultivation;

/// <summary>The random-source seam: world generation and the runner draw through this so the
/// runner can use a SERIALIZABLE generator (saves must resume deterministically —
/// System.Random's state cannot be captured).</summary>
public interface IRng
{
    /// <summary>Uniform in [0, maxExclusive).</summary>
    int Next(int maxExclusive);

    /// <summary>Uniform in [minInclusive, maxExclusive).</summary>
    int Next(int minInclusive, int maxExclusive);

    /// <summary>Uniform in [0, 1).</summary>
    double NextDouble();
}

/// <summary>System.Random adapter — used where state never needs to persist (world
/// generation is re-derived from the seed, not saved).</summary>
public sealed class SystemRng(int seed) : IRng
{
    private readonly Random _random = new(seed);

    public int Next(int maxExclusive) => _random.Next(maxExclusive);
    public int Next(int minInclusive, int maxExclusive) => _random.Next(minInclusive, maxExclusive);
    public double NextDouble() => _random.NextDouble();
}

/// <summary>PCG-XSH-RR 32 (O'Neill) — tiny, fast, and its whole state is two u64s, so a save
/// captures it exactly and a loaded game continues the SAME random stream.</summary>
public sealed class Pcg32 : IRng
{
    private const ulong Multiplier = 6364136223846793005ul;

    public ulong State { get; set; }
    public ulong Stream { get; set; }

    public Pcg32(int seed, ulong stream = 54ul)
    {
        Stream = (stream << 1) | 1ul;
        State = 0;
        NextUInt();
        State += (ulong)(uint)seed * 0x9E3779B97F4A7C15ul + (ulong)seed;
        NextUInt();
    }

    /// <summary>Restore from saved state verbatim.</summary>
    public Pcg32(ulong state, ulong stream)
    {
        State = state;
        Stream = stream;
    }

    public uint NextUInt()
    {
        var old = State;
        State = old * Multiplier + Stream;
        var xorShifted = (uint)(((old >> 18) ^ old) >> 27);
        var rot = (int)(old >> 59);
        return (xorShifted >> rot) | (xorShifted << (-rot & 31));
    }

    public int Next(int maxExclusive) =>
        maxExclusive <= 0 ? 0 : (int)(NextUInt() % (uint)maxExclusive);

    public int Next(int minInclusive, int maxExclusive) =>
        minInclusive >= maxExclusive ? minInclusive : minInclusive + Next(maxExclusive - minInclusive);

    public double NextDouble() => NextUInt() / 4294967296.0;
}
