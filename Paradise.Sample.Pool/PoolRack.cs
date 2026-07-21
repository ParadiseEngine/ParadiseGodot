using System;
using System.Collections.Generic;
using System.Numerics;

namespace Paradise.Sample.Pool;

/// <summary>Host-agnostic pool-rack helpers shared by the .NET runtime (SceneAssembler, from the
/// exported contract) and the Godot bridge (from live Area3D nodes). The pocket-capture math —
/// the tray park-position layout and the (centerX, centerZ, radius²) planar packing — MUST be
/// identical across hosts, so it lives here rather than being duplicated per host.</summary>
public static class PoolRack
{
    /// <summary>Build a ball's <see cref="PocketConfig"/> from the scene's pocket set. Empty pockets →
    /// inert default (never sinks). <paramref name="trayIndex"/> is the ball's spawn order, giving
    /// each sunk ball a deterministic tray slot along +Z past the pocket field.</summary>
    public static PocketConfig BuildBall(
        IReadOnlyList<(Vector3 Center, float Radius)> pockets, bool isCue, Vector3 authoredPosition, int trayIndex)
    {
        if (pockets.Count == 0) return default;

        float maxZ = float.MinValue, minX = float.MaxValue;
        foreach (var (center, _) in pockets)
        {
            maxZ = MathF.Max(maxZ, center.Z);
            minX = MathF.Min(minX, center.X);
        }

        var pool = new PocketConfig
        {
            PocketCount = Math.Min(pockets.Count, PocketConfig.MaxPockets),
            ParkPosition = new Vector3(minX + trayIndex * 0.45f, authoredPosition.Y, maxZ + 0.75f),
            RespawnPosition = authoredPosition,
            IsCue = isCue ? (byte)1 : (byte)0,
        };
        for (var i = 0; i < pool.PocketCount; i++)
        {
            var (center, radius) = pockets[i];
            pool.Pockets[i] = new Vector4(center.X, center.Z, radius * radius, 0f);
        }
        return pool;
    }
}
