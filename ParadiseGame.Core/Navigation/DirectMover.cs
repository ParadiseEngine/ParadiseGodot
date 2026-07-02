using System;
using System.Numerics;

namespace ParadiseGame.Core.Navigation;

/// <summary>
/// Applies a direct (WASD) move to an agent: slide along the navmesh in the input direction at the
/// agent's speed, face that direction, and clear any active path (direct input overrides path-following).
/// Managed because the navmesh clamp (<see cref="INavigationMesh.MoveAlongSurface"/>) can't run inside an
/// unmanaged ECS system. The BankHeist <c>InputMove</c> command analog.
/// </summary>
public static class DirectMover
{
    public static void Apply(World world, Entity entity, Vector3 direction, float deltaSeconds, INavigationMesh navigationMesh)
    {
        var horizontal = new Vector3(direction.X, 0f, direction.Z);
        float length = horizontal.Length();
        if (length < 1e-4f)
        {
            return; // no input this tick
        }

        horizontal /= length;

        ref var transform = ref world.GetComponent<LocalTransform>(entity);
        ref var path = ref world.GetComponent<NavPath>(entity);
        NavAgent agent = world.GetComponent<NavAgent>(entity);

        path.HasPath = 0; // WASD overrides click-to-move path following

        Vector3 from = transform.Position;
        Vector3 target = from + horizontal * agent.MoveSpeed * deltaSeconds;
        Vector3 clamped = navigationMesh.MoveAlongSurface(from, target);
        transform.Position = new Vector3(clamped.X, from.Y, clamped.Z); // stay on XZ, keep the agent's height

        // Face the move direction (model forward is −Z, right-handed).
        float yaw = MathF.Atan2(-horizontal.X, -horizontal.Z);
        transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
    }
}
