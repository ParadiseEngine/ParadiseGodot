using System;
using System.IO;
using System.Numerics;
using ParadiseGame.Navigation.Detour;

namespace ParadiseGame.Tests;

// Path-clearance guard on the checked-in baked artifact (data/scenes/sample.navmesh.bin). Movement
// collision is owned by Paradise.Physics now, so the navmesh's remaining job is to PLAN paths whose
// corners keep the agent CENTER at least a body radius (0.4, eroded in 0.1 cell steps) away from
// obstacle faces. If bake erosion regressed, planned paths would hug the walls and the capsule
// would grind along them under physics.
public class BakedNavMeshClearanceTests
{
    private const float AgentRadius = 0.4f;
    private const float BakeCellSize = 0.1f; // erosion is quantized in cell-size steps
    private const float Clearance = AgentRadius - BakeCellSize;

    [Test]
    public async Task baked_paths_keep_agent_radius_clearance_from_obstacle_faces()
    {
        var nav = DetourNavMeshLoader.LoadFromFile(FindRepoFile("data/scenes/sample.navmesh.bin"));

        // Sample scene: Obstacle1 is a 2x3x2 box at (5, 2, 0) — footprint x[4..6], z[-1..1].
        // A path crossing its line must detour around the eroded hole, never through the band
        // within Clearance of the faces.
        var path = nav.FindPath(new Vector3(2f, 0.7f, 0f), new Vector3(8f, 0.7f, 0f));

        await Assert.That(path.Count).IsGreaterThanOrEqualTo(3); // start + at least one detour corner + goal
        foreach (Vector3 corner in path)
        {
            bool insideClearanceBand =
                corner.X > 4f - Clearance && corner.X < 6f + Clearance &&
                MathF.Abs(corner.Z) < 1f + Clearance;
            await Assert.That(insideClearanceBand).IsFalse();
        }
    }

    private static string FindRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"'{relativePath}' not found above {AppContext.BaseDirectory}");
    }
}
