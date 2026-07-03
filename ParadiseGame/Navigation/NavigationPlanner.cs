using System;
using System.Numerics;

namespace ParadiseGame.Navigation;

/// <summary>
/// Plans a move for an entity by querying an <see cref="INavigationMesh"/> and writing the resulting
/// waypoints into the entity's <see cref="NavPath"/> (the BankHeist <c>NavigationPlanner.PlanMoveTo</c>
/// analog). Pure logic — no engine types. <c>NavMeshFollowSystem</c> then advances along the path.
/// </summary>
public static class NavigationPlanner
{
    /// <summary>
    /// Query a path from the entity's current position to <paramref name="target"/> and store it on
    /// the entity's <see cref="NavPath"/>. Returns <c>false</c> (and clears the path) if none exists.
    /// </summary>
    public static bool PlanMoveTo(World world, Entity entity, Vector3 target, INavigationMesh navigationMesh)
    {
        ref var transform = ref world.GetComponent<LocalTransform>(entity);
        ref var path = ref world.GetComponent<NavPath>(entity);

        path.Count = 0;
        path.Cursor = 0;
        path.HasPath = 0;

        var waypoints = navigationMesh.FindPath(transform.Position, target);
        if (waypoints is null || waypoints.Count == 0)
        {
            return false;
        }

        int count = Math.Min(waypoints.Count, NavPath.MaxWaypoints);
        if (waypoints.Count > NavPath.MaxWaypoints)
        {
            // Don't truncate silently — a cut-off path stops the agent short of its goal, which is
            // indistinguishable from "arrived". Surface it (and bump NavPath.MaxWaypoints if it recurs).
            System.Diagnostics.Debug.WriteLine(
                $"[NavigationPlanner] Path truncated: {waypoints.Count} corners > NavPath.MaxWaypoints ({NavPath.MaxWaypoints}).");
        }

        for (int i = 0; i < count; i++)
        {
            path.Waypoints[i] = waypoints[i];
        }

        path.Count = count;
        path.HasPath = 1;
        return true;
    }
}
