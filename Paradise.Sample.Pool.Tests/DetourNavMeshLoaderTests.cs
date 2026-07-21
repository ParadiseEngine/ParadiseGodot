using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Paradise.Export.NavMesh;
using Paradise.Sample.Pool.Navigation;
using Paradise.Sample.Pool.Navigation.Detour;

namespace Paradise.Sample.Pool.Tests;

// Proves the engine-independent nav path: a navmesh written to a DotRecast MeshSet .bin (the same
// format Paradise.Export emits to data/scenes/<Scene>.navmesh.bin) can be loaded back and
// queried with no Godot involved. This is how BOTH Godot and the engine runtime consume the navmesh.
public class DetourNavMeshLoaderTests
{
    [Test]
    public async Task writes_and_reloads_a_binary_navmesh_then_finds_a_path()
    {
        var verts = new List<Vector3>
        {
            new(0f, 0f, 0f),
            new(20f, 0f, 0f),
            new(20f, 0f, 20f),
            new(0f, 0f, 20f),
        };
        // +Y-normal winding (reversed fan) — the naive 0,1,2/0,2,3 points −Y and zig-zags.
        var tris = new List<int> { 0, 2, 1, 0, 3, 2 };

        var start = new Vector3(2f, 0f, 2f);
        var goal = new Vector3(18f, 0f, 18f);
        string path = Path.Combine(Path.GetTempPath(), $"paradise_nav_{Guid.NewGuid():N}.navmesh.bin");
        try
        {
            NavMeshBinaryWriter.Write(path, verts, tris);

            INavigationMesh nav = DetourNavMeshLoader.LoadFromFile(path);
            var corners = nav.FindPath(start, goal);

            await Assert.That(corners.Count).IsGreaterThanOrEqualTo(2);

            // Round-trip must preserve a taut path (catches a winding/format regression).
            float total = 0f;
            for (int i = 1; i < corners.Count; i++)
            {
                total += Vector3.Distance(corners[i - 1], corners[i]);
            }
            await Assert.That(total).IsLessThan(Vector3.Distance(start, goal) * 1.1f);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
