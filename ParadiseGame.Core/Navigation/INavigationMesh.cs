using System.Collections.Generic;
using System.Numerics;

namespace ParadiseGame.Core.Navigation;

/// <summary>
/// Engine seam for navmesh pathfinding (the BankHeist <c>INavigationMesh</c> analog). Gameplay
/// systems depend on this interface, not on any concrete pathfinding library, so the same simulation
/// runs against a DotRecast backend (ParadiseGame.Navigation.Detour) regardless of host engine.
/// All coordinates are right-handed world space (metres).
/// </summary>
public interface INavigationMesh
{
    /// <summary>
    /// Find a walkable path from <paramref name="from"/> to <paramref name="to"/>, returned as a list
    /// of world-space corner points (including the start). Returns an empty list if no path exists.
    /// </summary>
    IReadOnlyList<Vector3> FindPath(Vector3 from, Vector3 to);

    /// <summary>
    /// Slide from <paramref name="from"/> toward <paramref name="to"/> constrained to the walkable
    /// surface, stopping at walls/edges. Used for direct (WASD) movement so the agent can't leave the
    /// navmesh. Returns the clamped world-space position (or <paramref name="from"/> if off-mesh).
    /// </summary>
    Vector3 MoveAlongSurface(Vector3 from, Vector3 to);
}
