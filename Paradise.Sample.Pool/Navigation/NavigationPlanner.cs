using System;
using System.Numerics;

namespace Paradise.Sample.Pool.Navigation;

/// <summary>
/// Plans a move for an entity by querying an <see cref="INavigationMesh"/> and writing the resulting
/// waypoints into the entity's <see cref="NavWaypoints"/> (the BankHeist <c>NavigationPlanner.PlanMoveTo</c>
/// analog). Pure logic — no engine types. <c>MovementSystem</c> then advances along the path.
/// </summary>
public static class NavigationPlanner
{
    /// <summary>
    /// Query a path from the entity's current position to <paramref name="target"/> and store it on
    /// the entity's <see cref="NavWaypoints"/>. Returns <c>false</c> (and clears the path) if none exists.
    /// </summary>
    public static bool PlanMoveTo(World world, Entity entity, Vector3 target, INavigationMesh navigationMesh)
    {
        Vector3 position = world.GetComponent<Position>(entity).Value;
        ref var path = ref world.GetComponent<NavWaypoints>(entity);

        path.Count = 0;
        world.GetComponent<NavCursor>(entity).Value = 0;
        world.GetComponent<HasPath>(entity).Value = 0;

        var waypoints = navigationMesh.FindPath(position, target);
        if (waypoints is null || waypoints.Count == 0)
        {
            return false;
        }

        int count = Math.Min(waypoints.Count, NavWaypoints.MaxWaypoints);
        if (waypoints.Count > NavWaypoints.MaxWaypoints)
        {
            // Don't truncate silently — a cut-off path stops the agent short of its goal, which is
            // indistinguishable from "arrived". Surface it (and bump NavWaypoints.MaxWaypoints if it recurs).
            System.Diagnostics.Debug.WriteLine(
                $"[NavigationPlanner] Path truncated: {waypoints.Count} corners > NavWaypoints.MaxWaypoints ({NavWaypoints.MaxWaypoints}).");
        }

        for (int i = 0; i < count; i++)
        {
            path.Waypoints[i] = waypoints[i];
        }

        path.Count = count;
        world.GetComponent<HasPath>(entity).Value = 1;
        return true;
    }
}
