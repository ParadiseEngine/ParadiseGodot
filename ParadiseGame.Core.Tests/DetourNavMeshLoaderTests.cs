using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ParadiseExport.Core.NavMesh;
using ParadiseGame.Core.Navigation;
using ParadiseGame.Navigation.Detour;

namespace ParadiseGame.Core.Tests;

// Proves the engine-independent nav path: a navmesh written to a DotRecast MeshSet .bin (the same
// format ParadiseExport.Core emits to data/scenes/<Scene>.navmesh.bin) can be loaded back and
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
        var tris = new List<int> { 0, 1, 2, 0, 2, 3 };

        string path = Path.Combine(Path.GetTempPath(), $"paradise_nav_{Guid.NewGuid():N}.navmesh.bin");
        try
        {
            NavMeshBinaryWriter.Write(path, verts, tris);

            INavigationMesh nav = DetourNavMeshLoader.LoadFromFile(path);
            var corners = nav.FindPath(new Vector3(2f, 0f, 2f), new Vector3(18f, 0f, 18f));

            await Assert.That(corners.Count).IsGreaterThanOrEqualTo(2);
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
