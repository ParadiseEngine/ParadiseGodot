using System.Numerics;
using System.Runtime.CompilerServices;

namespace ParadiseGame.Core;

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
/// consumed by <c>NavMeshFollowSystem</c>.
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
