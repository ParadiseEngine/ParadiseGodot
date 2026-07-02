using System;
using System.Numerics;

namespace ParadiseGame.Core.Navigation;

/// <summary>
/// Applies a direct (WASD) move to an agent: writes this tick's <see cref="MoveIntent"/> in the
/// input direction at the agent's speed, faces that direction, and clears any active path (direct
/// input overrides path-following). Collision is resolved afterwards by
/// <see cref="Physics.CharacterMoveIntegrator"/> — there is no navmesh clamp; physics owns
/// movement collision. The BankHeist <c>InputMove</c> command analog.
/// </summary>
public static class DirectMover
{
    public static void Apply(World world, Entity entity, Vector3 direction)
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
        ref var intent = ref world.GetComponent<MoveIntent>(entity);
        NavAgent agent = world.GetComponent<NavAgent>(entity);

        path.HasPath = 0; // WASD overrides click-to-move path following
        intent.DesiredVelocity = horizontal * agent.MoveSpeed;

        // Face the move direction (model forward is −Z, right-handed).
        float yaw = MathF.Atan2(-horizontal.X, -horizontal.Z);
        transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
    }
}
