using System;
using System.Collections.Generic;
using System.Numerics;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using ParadiseExport.NavMesh;
using Paradise.Sample.Game.Navigation;

namespace Paradise.Sample.Game.Navigation.Detour;

/// <summary>
/// DotRecast-backed <see cref="INavigationMesh"/> — the single shared nav backend for Godot and the
/// engine runtime. Builds a queryable <see cref="DtNavMesh"/> from right-handed triangles (via
/// ParadiseExport's <see cref="NavMeshBinaryWriter.BuildNavMesh"/>) and answers path queries with
/// a <see cref="DtNavMeshQuery"/>. Query state and buffers are reused across calls — not thread-safe.
/// Query/steering shape adapted from bank-heist's DetourNavigationMesh.
/// </summary>
public sealed class DetourNavigationMesh : INavigationMesh
{
    private const int MaxPathPolys = 4096;
    private const int MaxStraightPathPoints = 4096;
    private const int MaxGoalCandidatePolys = 128;
    private const float GoalCandidateRadius = 3f;
    private static readonly RcVec3f s_defaultExtents = new(2f, 4f, 2f);

    private readonly DtNavMeshQuery _query;
    private readonly DtQueryDefaultFilter _filter = new();
    private readonly long[] _pathPolys = new long[MaxPathPolys];
    private readonly long[] _goalCandidatePolys = new long[MaxGoalCandidatePolys];
    private readonly DtStraightPath[] _straightPath = new DtStraightPath[MaxStraightPathPoints];
    private readonly List<GoalCandidate> _goalCandidates = new(MaxGoalCandidatePolys);

    /// <summary>Build from right-handed triangle soup (world-space verts + triangle indices).</summary>
    public DetourNavigationMesh(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> triangleIndices)
        : this(NavMeshBinaryWriter.BuildNavMesh(vertices, triangleIndices))
    {
    }

    /// <summary>Wrap an already-built DtNavMesh.</summary>
    public DetourNavigationMesh(DtNavMesh navMesh)
    {
        ArgumentNullException.ThrowIfNull(navMesh);
        // The DtMeshSetWriter/Reader round-trip drops internal poly-to-poly adjacency (neis), which
        // makes FindStraightPath produce zig-zagging garbage corridors. Rebuild the internal
        // adjacency by matching shared edges before querying. (Adapted from bank-heist.)
        RepairInternalAdjacency(navMesh);
        _query = new DtNavMeshQuery(navMesh);
    }

    public IReadOnlyList<Vector3> FindPath(Vector3 from, Vector3 to)
    {
        var startPos = new RcVec3f(from.X, from.Y, from.Z);
        var goalPos = new RcVec3f(to.X, to.Y, to.Z);

        DtStatus status = _query.FindNearestPoly(startPos, s_defaultExtents, _filter,
            out long startRef, out RcVec3f startNearest, out bool _);
        if (status.Failed() || startRef == 0)
        {
            return Array.Empty<Vector3>();
        }

        if (!TryFindReachableGoal(startRef, startNearest, goalPos, out RcVec3f goalNearest, out int npolys))
        {
            return Array.Empty<Vector3>();
        }

        status = _query.FindStraightPath(startNearest, goalNearest, new Span<long>(_pathPolys), npolys,
            new Span<DtStraightPath>(_straightPath), out int nstraightPath, _straightPath.Length, 0);
        if (status.Failed())
        {
            return Array.Empty<Vector3>();
        }

        var result = new List<Vector3>(nstraightPath);
        for (int i = 0; i < nstraightPath; i++)
        {
            RcVec3f point = _straightPath[i].pos;
            result.Add(new Vector3(point.X, point.Y, point.Z));
        }

        return result;
    }

    private bool TryFindReachableGoal(long startRef, RcVec3f startNearest, RcVec3f goalPos,
        out RcVec3f goalNearest, out int npolys)
    {
        goalNearest = default;
        npolys = 0;

        DtStatus status = _query.QueryPolygons(goalPos, s_defaultExtents, _filter,
            _goalCandidatePolys, out int candidateCount, _goalCandidatePolys.Length);
        if (status.Failed() || candidateCount == 0)
        {
            return false;
        }

        // Reuse the candidate buffer across calls to avoid per-FindPath allocation.
        _goalCandidates.Clear();
        for (int i = 0; i < candidateCount; i++)
        {
            long candidateRef = _goalCandidatePolys[i];
            if (candidateRef == 0)
            {
                continue;
            }

            status = _query.ClosestPointOnPoly(candidateRef, goalPos, out RcVec3f nearest, out bool _);
            if (status.Failed())
            {
                continue;
            }

            _goalCandidates.Add(new GoalCandidate(candidateRef, nearest, DistanceSquaredXZ(goalPos, nearest)));
        }

        _goalCandidates.Sort(static (left, right) => left.DistanceSquared.CompareTo(right.DistanceSquared));

        foreach (GoalCandidate candidate in _goalCandidates)
        {
            if (candidate.DistanceSquared > GoalCandidateRadius * GoalCandidateRadius)
            {
                break;
            }

            status = _query.FindPath(startRef, candidate.Ref, startNearest, candidate.Nearest, _filter,
                new Span<long>(_pathPolys), out int candidatePathCount, _pathPolys.Length);
            if (status.Failed() || candidatePathCount == 0 || _pathPolys[candidatePathCount - 1] != candidate.Ref)
            {
                continue;
            }

            goalNearest = candidate.Nearest;
            npolys = candidatePathCount;
            return true;
        }

        return false;
    }

    private static float DistanceSquaredXZ(RcVec3f left, RcVec3f right)
    {
        float dx = left.X - right.X;
        float dz = left.Z - right.Z;
        return dx * dx + dz * dz;
    }

    private const int EdgeCoordinateScale = 1000;

    // Re-links interior polygon edges that share the same two vertices but were left unconnected
    // (neis == 0) after (de)serialization. Without this, the path corridor is fragmented and the
    // straight-path funnel produces zig-zag garbage.
    private static void RepairInternalAdjacency(DtNavMesh navMesh)
    {
        int maxTiles = navMesh.GetMaxTiles();
        for (int tileIndex = 0; tileIndex < maxTiles; tileIndex++)
        {
            DtMeshTile? tile = navMesh.GetTile(tileIndex);
            DtMeshData? data = tile?.data;
            if (data?.header == null || data.polys == null || data.verts == null)
            {
                continue;
            }

            if (RepairTileInternalAdjacency(data))
            {
                navMesh.UpdateTile(data, tile!.flags);
            }
        }
    }

    private static bool RepairTileInternalAdjacency(DtMeshData data)
    {
        int polyCount = Math.Min(data.header.polyCount, data.header.offMeshBase);
        var edges = new Dictionary<EdgeKey, List<EdgeRef>>();

        for (int polyIndex = 0; polyIndex < polyCount; polyIndex++)
        {
            DtPoly poly = data.polys[polyIndex];
            if (poly == null || poly.vertCount < 3)
            {
                continue;
            }

            for (int edgeIndex = 0; edgeIndex < poly.vertCount; edgeIndex++)
            {
                if (poly.neis[edgeIndex] != 0)
                {
                    continue;
                }

                var key = new EdgeKey(
                    CreateVertexKey(data.verts, poly.verts[edgeIndex]),
                    CreateVertexKey(data.verts, poly.verts[(edgeIndex + 1) % poly.vertCount]));
                if (!edges.TryGetValue(key, out var matchingEdges))
                {
                    matchingEdges = new List<EdgeRef>(2);
                    edges.Add(key, matchingEdges);
                }

                matchingEdges.Add(new EdgeRef(polyIndex, edgeIndex));
            }
        }

        bool repaired = false;
        foreach (var matchingEdges in edges.Values)
        {
            if (matchingEdges.Count != 2)
            {
                continue;
            }

            EdgeRef first = matchingEdges[0];
            EdgeRef second = matchingEdges[1];
            data.polys[first.PolyIndex].neis[first.EdgeIndex] = second.PolyIndex + 1;
            data.polys[second.PolyIndex].neis[second.EdgeIndex] = first.PolyIndex + 1;
            repaired = true;
        }

        return repaired;
    }

    private static VertexKey CreateVertexKey(float[] vertices, int vertexIndex)
    {
        int baseIndex = vertexIndex * 3;
        return new VertexKey(
            QuantizeEdgeCoordinate(vertices[baseIndex + 0]),
            QuantizeEdgeCoordinate(vertices[baseIndex + 1]),
            QuantizeEdgeCoordinate(vertices[baseIndex + 2]));
    }

    private static int QuantizeEdgeCoordinate(float value) => (int)MathF.Round(value * EdgeCoordinateScale);

    private readonly struct EdgeRef
    {
        public EdgeRef(int polyIndex, int edgeIndex)
        {
            PolyIndex = polyIndex;
            EdgeIndex = edgeIndex;
        }

        public int PolyIndex { get; }
        public int EdgeIndex { get; }
    }

    private readonly struct VertexKey : IEquatable<VertexKey>
    {
        public VertexKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        private int X { get; }
        private int Y { get; }
        private int Z { get; }

        public int CompareTo(VertexKey other)
        {
            int x = X.CompareTo(other.X);
            if (x != 0) return x;
            int y = Y.CompareTo(other.Y);
            return y != 0 ? y : Z.CompareTo(other.Z);
        }

        public bool Equals(VertexKey other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object? obj) => obj is VertexKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    }

    private readonly struct EdgeKey : IEquatable<EdgeKey>
    {
        private readonly VertexKey _a;
        private readonly VertexKey _b;

        public EdgeKey(VertexKey a, VertexKey b)
        {
            if (a.CompareTo(b) <= 0)
            {
                _a = a;
                _b = b;
            }
            else
            {
                _a = b;
                _b = a;
            }
        }

        public bool Equals(EdgeKey other) => _a.Equals(other._a) && _b.Equals(other._b);
        public override bool Equals(object? obj) => obj is EdgeKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(_a, _b);
    }

    private readonly struct GoalCandidate
    {
        public GoalCandidate(long polyRef, RcVec3f nearest, float distanceSquared)
        {
            Ref = polyRef;
            Nearest = nearest;
            DistanceSquared = distanceSquared;
        }

        public long Ref { get; }
        public RcVec3f Nearest { get; }
        public float DistanceSquared { get; }
    }
}
