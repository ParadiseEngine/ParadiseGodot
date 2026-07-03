using System.Numerics;
using System.Runtime.CompilerServices;

namespace ParadiseGame;

/// <summary>
/// Shared per-frame simulation data, injected into systems as a component (Paradise.ECS injects
/// component instances, not arbitrary objects — so shared state is modelled as a component the
/// systems read). Carried by every simulated agent; <see cref="GameSimulation.Tick"/> refreshes it
/// once per tick before the schedule runs. Extend with elapsed time / frame count as needed.
/// </summary>
[Component]
public partial struct SimulationContext
{
    public float DeltaSeconds;
}

/// <summary>World-space transform in right-handed (Y-up, −Z forward) coordinates.</summary>
[Component]
public partial struct LocalTransform
{
    public Vector3 Position;
    public Quaternion Rotation;

    public LocalTransform(Vector3 position, Quaternion rotation)
    {
        Position = position;
        Rotation = rotation;
    }
}

/// <summary>Steering parameters for a navmesh-following agent (metres/sec, degrees/sec, metres).</summary>
[Component]
public partial struct NavAgent
{
    public float MoveSpeed;
    public float AngularSpeed;
    public float ArriveRadius;

    public NavAgent(float moveSpeed, float angularSpeed, float arriveRadius)
    {
        MoveSpeed = moveSpeed;
        AngularSpeed = angularSpeed;
        ArriveRadius = arriveRadius;
    }
}

/// <summary>
/// A navmesh path the agent is following: an inline waypoint buffer (unmanaged, so it lives inline in
/// the component chunk) plus a cursor. Filled by <see cref="Navigation.NavigationPlanner"/> and
/// consumed by <see cref="MovementSystem"/>.
/// </summary>
[Component]
public partial struct NavPath
{
    public const int MaxWaypoints = 32;

    public WaypointBuffer Waypoints;
    public int Count;
    public int Cursor;
    public byte HasPath;
}

/// <summary>Fixed-capacity inline buffer of waypoints (C# 12 InlineArray — unmanaged, blittable).</summary>
[InlineArray(NavPath.MaxWaypoints)]
public struct WaypointBuffer
{
    private Vector3 _element0;
}

/// <summary>
/// This tick's desired velocity (m/s, horizontal), produced by steering (path following or direct
/// input) and consumed by <see cref="MovementSystem"/>. Zeroed at the start of
/// every tick so stale intent never leaks across frames.
/// </summary>
[Component]
public partial struct MoveIntent
{
    public Vector3 DesiredVelocity;
}

/// <summary>
/// Collision capsule of a movable character (Y-aligned, origin at the capsule CENTER — matching
/// scene authoring). <see cref="HalfLength"/> is the core segment half length: total capsule height
/// = 2 * (HalfLength + Radius). All character physics state lives in components; the collision
/// world holds only immutable static geometry.
/// </summary>
[Component]
public partial struct CharacterBody
{
    public float Radius;
    public float HalfLength;

    public CharacterBody(float radius, float halfLength)
    {
        Radius = radius;
        HalfLength = halfLength;
    }
}

/// <summary>
/// Unmanaged handle to the session's static <c>Paradise.Physics.CollisionWorld</c>, carried as
/// a component (the <see cref="SimulationContext"/> pattern — Paradise.ECS has no singleton
/// store) so the generated <see cref="MovementSystem"/> can run collision queries without a
/// managed service. Borrowed: valid while the runner-owned CollisionWorld lives (the whole
/// session); <c>default</c> = no collision world (unobstructed movement).
/// </summary>
[Component]
public partial struct PhysicsWorldRef
{
    public Paradise.Physics.CollisionWorldHandle Handle;
}

/// <summary>
/// A dynamic physics body (sphere-only in this phase). ALL of its physics state lives here —
/// the stateless resolver (<c>Paradise.Physics.PlanarSphereDynamics</c>) reads and writes
/// components each tick, so snapshots stay complete. Position is the sphere center
/// (<see cref="LocalTransform"/>); Y is never modified (planar contract).
/// </summary>
[Component]
public partial struct DynamicBody
{
    public Vector3 Velocity;
    public float Radius;
    public float Mass;

    public DynamicBody(float radius, float mass)
    {
        Velocity = Vector3.Zero;
        Radius = radius;
        Mass = mass;
    }
}
