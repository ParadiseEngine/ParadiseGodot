using System;
using System.Collections.Generic;

namespace ParadiseGame.Physics;

/// <summary>Scene-wide static physics properties derived identically by both hosts: the .NET
/// <c>SceneAssembler</c> gathers surfaces from the exported contract, the Godot <c>EcsSceneBridge</c>
/// from live nodes, and both reduce them here — so the derivation lives in ONE place while each
/// host keeps only its own (unavoidable) scene-reading adapter.</summary>
public static class StaticSurfaces
{
    /// <summary>A static collision surface: its restitution and collision-layer mask.</summary>
    public readonly record struct Surface(float Restitution, uint LayerMask);

    /// <summary>The scene's cushion bounce — the liveliest (max) restitution among the Obstacle-layer
    /// static surfaces balls actually bounce off — or <paramref name="fallback"/> when the scene
    /// authors none. Each host supplies the surfaces from its own source.</summary>
    public static float BounceRestitution(IEnumerable<Surface> surfaces, float fallback)
    {
        float max = -1f;
        foreach (var surface in surfaces)
        {
            // Bitwise test: a body authored on Obstacle PLUS another layer still counts (both hosts
            // yield single-bit masks today, but this is robust to multi-layer authoring).
            if ((surface.LayerMask & PhysicsLayers.Obstacle) == 0) continue;
            max = MathF.Max(max, surface.Restitution);
        }
        return max >= 0f ? max : fallback;
    }
}
