using System;
using System.Collections.Generic;
using System.Numerics;
using Paradise.Sample.Pool.Navigation;

namespace Paradise.Sample.ImGui;

/// <summary>Trivial <see cref="INavigationMesh"/> for the balls-only ImGui sample: there are no
/// navmesh agents, so path planning is never exercised — <see cref="FindPath"/> returns empty.
/// Keeps the sample off the Detour package (mirrors the test stubs in Paradise.Sample.Ui.Tests).</summary>
internal sealed class NoNavMesh : INavigationMesh
{
    public IReadOnlyList<Vector3> FindPath(Vector3 from, Vector3 to) => Array.Empty<Vector3>();
}
