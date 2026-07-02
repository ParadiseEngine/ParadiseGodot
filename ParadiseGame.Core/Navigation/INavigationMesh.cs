using System.Collections.Generic;
using System.Numerics;

namespace ParadiseGame.Core.Navigation;

/// <summary>
/// Engine seam for navmesh PATHFINDING ONLY (the BankHeist <c>INavigationMesh</c> analog). Gameplay
/// systems depend on this interface, not on any concrete pathfinding library, so the same simulation
/// runs against a DotRecast backend (ParadiseGame.Navigation.Detour) regardless of host engine.
/// Movement collision is owned by <c>Paradise.Physics</c> (see
/// <c>Physics.CharacterMoveIntegrator</c>), not by the navmesh.
/// All coordinates are right-handed world space (metres).
/// </summary>
public interface INavigationMesh
{
    /// <summary>
    /// Find a walkable path from <paramref name="from"/> to <paramref name="to"/>, returned as a list
    /// of world-space corner points (including the start). Returns an empty list if no path exists.
    /// </summary>
    IReadOnlyList<Vector3> FindPath(Vector3 from, Vector3 to);
}
