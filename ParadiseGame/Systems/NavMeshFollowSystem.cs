using System;
using System.Numerics;

namespace ParadiseGame;

/// <summary>
/// The navmesh steering system: follows the agent's <see cref="NavPath"/> at its
/// <see cref="NavAgent"/> speed by writing a desired velocity into <see cref="MoveIntent"/> —
/// it does NOT move the transform. Integration (and collision) happens afterwards in
/// <see cref="Physics.CharacterMoveIntegrator"/>, so waypoint advance and arrival are measured on
/// the previous tick's physics-resolved position. Rotation stays here (facing is cosmetic and
/// ECS-owned). The BankHeist <c>MovementSystem</c> analog, split steering-vs-integration.
/// </summary>
public ref partial struct NavMeshFollowSystem : IEntitySystem
{
    public ref LocalTransform Transform;
    public ref NavPath Path;
    public ref MoveIntent Intent;
    public ref readonly NavAgent Agent;
    public ref readonly SimulationContext Frame;

    public void Execute()
    {
        if (Path.HasPath == 0 || Path.Count == 0)
        {
            return;
        }

        float dt = Frame.DeltaSeconds;
        if (dt <= 0f)
        {
            return;
        }

        Vector3 position = Transform.Position;
        float arriveSq = Agent.ArriveRadius * Agent.ArriveRadius;

        // Skip any waypoints already within the arrive radius (handles the path's start corner).
        while (Path.Cursor < Path.Count && HorizontalDistanceSq(position, Path.Waypoints[Path.Cursor]) <= arriveSq)
        {
            Path.Cursor++;
        }

        if (Path.Cursor >= Path.Count)
        {
            Path.HasPath = 0;
            return;
        }

        Vector3 target = Path.Waypoints[Path.Cursor];
        Vector3 direction = new(target.X - position.X, 0f, target.Z - position.Z);
        float distance = direction.Length();
        if (distance <= 1e-5f)
        {
            return;
        }

        direction /= distance;
        // Steer toward the waypoint without overshooting it this tick; the physics integrator
        // moves the transform.
        float speed = MathF.Min(Agent.MoveSpeed, distance / dt);
        Intent.DesiredVelocity = direction * speed;

        // Face the movement direction (cosmetic). Model forward is −Z (right-handed).
        float yaw = MathF.Atan2(-direction.X, -direction.Z);
        Quaternion desired = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        Transform.Rotation = RotateTowards(Transform.Rotation, desired, DegToRad(Agent.AngularSpeed) * dt);
    }

    private static float HorizontalDistanceSq(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    private static float DegToRad(float degrees) => degrees * (MathF.PI / 180f);

    private static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxRadians)
    {
        from = Quaternion.Normalize(from);
        to = Quaternion.Normalize(to);
        float dot = Math.Clamp(Quaternion.Dot(from, to), -1f, 1f);
        if (dot < 0f)
        {
            to = -to;
            dot = -dot;
        }

        float angle = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * 2f;
        if (angle <= 1e-4f || maxRadians <= 0f)
        {
            return from;
        }

        float t = Math.Clamp(maxRadians / angle, 0f, 1f);
        return Quaternion.Normalize(Quaternion.Slerp(from, to, t));
    }
}
