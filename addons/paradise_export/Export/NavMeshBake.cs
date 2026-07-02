#if TOOLS
using System.Collections.Generic;
using Godot;
using SN = System.Numerics;

namespace ParadiseGodot.Export
{
    /// <summary>
    /// Bakes a navmesh from the edited scene's static collision geometry via NavigationServer3D and
    /// returns its triangulation in the export contract's convention, ready for
    /// <see cref="ParadiseExport.Core.NavMesh.NavMeshBinaryWriter"/>.
    ///
    /// Agent exclusion: only StaticColliders are parsed, so moving agents (CharacterBody3D /
    /// RigidBody3D) are naturally excluded from the walkable surface — the Godot-idiomatic
    /// equivalent of the Unity tool's EntityAuthoring.IsAgent filter.
    ///
    /// Bake cell sizes match <see cref="ParadiseExport.Core.NavMesh.NavMeshBinaryWriter"/>'s
    /// quantization (0.1) so the exported geometry resolution is consistent.
    /// </summary>
    internal static class NavMeshBake
    {
        public static bool TryBake(Node root, out List<SN.Vector3> vertices, out List<int> triangles)
        {
            vertices = new List<SN.Vector3>();
            triangles = new List<int>();

            var navMesh = new NavigationMesh
            {
                CellSize = 0.1f,
                CellHeight = 0.1f,
                AgentHeight = 1.8f,
                // Erode the walkable area by the agent's body radius (the sample agent capsule is 0.4).
                // Path following steers the agent CENTER along planned corners, so with radius 0 the
                // planned paths run flush against obstacle faces and the capsule grinds along walls.
                AgentRadius = 0.4f,
                AgentMaxClimb = 0.3f,
                GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders,
                GeometrySourceGeometryMode = NavigationMesh.SourceGeometryMode.RootNodeChildren,
            };

            var source = new NavigationMeshSourceGeometryData3D();
            NavigationServer3D.ParseSourceGeometryData(navMesh, source, root);
            NavigationServer3D.BakeFromSourceGeometryData(navMesh, source);

            Vector3[] bakedVertices = navMesh.GetVertices();
            if (bakedVertices.Length == 0)
            {
                return false;
            }

            // The contract is right-handed (Godot-native), so vertices are stored verbatim.
            foreach (Vector3 v in bakedVertices)
            {
                vertices.Add(new SN.Vector3(v.X, v.Y, v.Z));
            }

            // Fan-triangulate each polygon. Godot's navmesh polygons are wound so the naive fan yields
            // a downward (−Y) normal; DotRecast's navmesh/funnel needs upward (+Y) normals, so emit
            // the fan reversed (poly[0], poly[i], poly[i-1]) to flip the winding. (Verified: the naive
            // order makes FindStraightPath produce zig-zag corridors.)
            int polygonCount = navMesh.GetPolygonCount();
            for (int p = 0; p < polygonCount; p++)
            {
                int[] poly = navMesh.GetPolygon(p);
                for (int i = 2; i < poly.Length; i++)
                {
                    triangles.Add(poly[0]);
                    triangles.Add(poly[i]);
                    triangles.Add(poly[i - 1]);
                }
            }

            return triangles.Count > 0;
        }
    }
}
#endif
