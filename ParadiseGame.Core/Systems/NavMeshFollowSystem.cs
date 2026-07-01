using System;
using System.Numerics;

namespace ParadiseGame.Core;

/// <summary>
/// The navmesh controller system: steers an agent's <see cref="LocalTransform"/> along its
/// <see cref="NavPath"/> at its <see cref="NavAgent"/> speeds, advancing the waypoint cursor as each
/// corner is reached and clearing the path on arrival. Engine-agnostic and coordinate-agnostic
/// (operates in whatever right-handed world space the host feeds). The BankHeist
/// <c>MovementSystem</c> analog. Delta time is injected via the shared <see cref="SimulationContext"/>.
/// </summary>
public ref partial struct NavMeshFollowSystem : IEntitySystem
{
    public ref LocalTransform Transform;
    public ref NavPath Path;
    public ref readonly NavAgent Agent;
    public ref readonly SimulationContext Frame;

    public void Execute()
    {
        if (Path.HasPath == 0 || Path.Count == 0)
        {
            return;
        }

        float dt = Frame.DeltaSeconds;
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
        float step = Agent.MoveSpeed * dt;
        position = step >= distance
            ? new Vector3(target.X, position.Y, target.Z)
            : position + direction * step;
        Transform.Position = position;

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
