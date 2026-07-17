namespace ParadiseCultivation;

/// <summary>Seeded 2D value noise + fBm — dependency-free and fully deterministic across
/// runs and platforms (integer hashing, no trig). Output is in [0, 1].</summary>
internal static class ValueNoise
{
    private static float Lattice(int seed, int x, int y)
    {
        // murmur3-style finalizer over the packed lattice coordinates.
        uint h = (uint)seed;
        h ^= (uint)x * 0x9E3779B1u;
        h = (h << 13) | (h >> 19);
        h ^= (uint)y * 0x85EBCA77u;
        h *= 0xC2B2AE3Du;
        h ^= h >> 16;
        h *= 0x27D4EB2Fu;
        h ^= h >> 15;
        return (h & 0xFFFFFF) / (float)0xFFFFFF;
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    public static float Sample(int seed, float x, float y)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var tx = Smooth(x - x0);
        var ty = Smooth(y - y0);

        var a = Lattice(seed, x0, y0);
        var b = Lattice(seed, x0 + 1, y0);
        var c = Lattice(seed, x0, y0 + 1);
        var d = Lattice(seed, x0 + 1, y0 + 1);

        var top = a + (b - a) * tx;
        var bottom = c + (d - c) * tx;
        return top + (bottom - top) * ty;
    }

    public static float Fbm(int seed, float x, float y, int octaves)
    {
        var sum = 0f;
        var amplitude = 1f;
        var total = 0f;
        var frequency = 1f;
        for (var i = 0; i < octaves; i++)
        {
            sum += Sample(seed + i * 101, x * frequency, y * frequency) * amplitude;
            total += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }
        return sum / total;
    }
}
