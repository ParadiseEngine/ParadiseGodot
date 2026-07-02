using Paradise.Physics;

namespace ParadiseGame.Core.Physics;

/// <summary>
/// Collision layer contract shared between the Godot scene (StaticBody3D <c>collision_layer</c>)
/// and the simulation's <see cref="Paradise.Physics.CollisionWorld"/>. Character movement casts
/// deliberately ignore the floor: the capsule rests exactly on it, and the planar contract means
/// only walls/obstacles ever block horizontal motion.
/// </summary>
public static class PhysicsLayers
{
    /// <summary>Walkable ground (Godot collision_layer bit 1).</summary>
    public const uint Floor = 1u << 0;

    /// <summary>Blocking obstacles/props (Godot collision_layer bit 2).</summary>
    public const uint Obstacle = 1u << 1;

    /// <summary>Filter for character movement capsule casts: obstacles only, never the floor.</summary>
    public static readonly CollisionFilter CharacterCast = new() { BelongsTo = ~0u, CollidesWith = Obstacle };

    /// <summary>Filter for click-to-move ground picking rays.</summary>
    public static readonly CollisionFilter ClickRay = new() { BelongsTo = ~0u, CollidesWith = Floor | Obstacle };
}
