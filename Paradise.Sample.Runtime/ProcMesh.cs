using System;
using System.Collections.Generic;
using System.Numerics;

namespace Paradise.Sample.Runtime;

/// <summary>
/// Builds the "Space Odyssey" sample's meshes procedurally — no GLB art pipeline. Every vertex is the
/// engine's 12-float interleaved layout the PBR uploader expects: position(3) · normal(3) · uv(2) ·
/// tangent(4, xyz + handedness). Triangles wind CCW so outward faces front (validated by the headless
/// non-empty-frame check). Meshes are unit-scaled about the origin; the render loop applies the world
/// scale per instance.
/// </summary>
internal static class ProcMesh
{
    private const int FloatsPerVertex = 12;

    /// <summary>A unit UV sphere (radius 1) — star, planets, asteroids.</summary>
    public static (float[] Vertices, uint[] Indices) Sphere(int stacks = 24, int slices = 32)
    {
        var verts = new List<float>((stacks + 1) * (slices + 1) * FloatsPerVertex);
        var idx = new List<uint>(stacks * slices * 6);

        for (int i = 0; i <= stacks; i++)
        {
            float v = (float)i / stacks;
            float theta = v * MathF.PI;            // 0 (top) → π (bottom)
            float sinT = MathF.Sin(theta), cosT = MathF.Cos(theta);
            for (int j = 0; j <= slices; j++)
            {
                float u = (float)j / slices;
                float phi = u * MathF.PI * 2f;
                float sinP = MathF.Sin(phi), cosP = MathF.Cos(phi);
                var n = new Vector3(sinT * cosP, cosT, sinT * sinP); // unit → position == normal
                var tangent = new Vector3(-sinP, 0f, cosP);          // dφ direction
                Push(verts, n, n, new Vector2(u, v), tangent);
            }
        }

        int rowStride = slices + 1;
        for (int i = 0; i < stacks; i++)
        {
            for (int j = 0; j < slices; j++)
            {
                uint p0 = (uint)(i * rowStride + j);
                uint p1 = p0 + 1;
                uint p2 = (uint)((i + 1) * rowStride + j);
                uint p3 = p2 + 1;
                idx.Add(p0); idx.Add(p2); idx.Add(p1);
                idx.Add(p1); idx.Add(p2); idx.Add(p3);
            }
        }
        return (verts.ToArray(), idx.ToArray());
    }

    /// <summary>A dart/cone for the ship: a nose apex at +Z (the ship's forward) tapering to a ringed
    /// base at −Z, capped. Right-handed, so it visibly points where the ship flies.</summary>
    public static (float[] Vertices, uint[] Indices) Ship(int segments = 16)
    {
        const float noseZ = 1.4f;   // apex (forward)
        const float baseZ = -0.9f;
        const float radius = 0.55f;

        var verts = new List<float>();
        var idx = new List<uint>();

        var apex = new Vector3(0f, 0f, noseZ);
        // Side wall: apex fan to a base ring. One apex vertex per segment (its own normal).
        for (int j = 0; j < segments; j++)
        {
            float a0 = (float)j / segments * MathF.PI * 2f;
            float a1 = (float)(j + 1) / segments * MathF.PI * 2f;
            var r0 = new Vector3(MathF.Cos(a0) * radius, MathF.Sin(a0) * radius, baseZ);
            var r1 = new Vector3(MathF.Cos(a1) * radius, MathF.Sin(a1) * radius, baseZ);
            var faceN = Vector3.Normalize(Vector3.Cross(r1 - apex, r0 - apex));
            var tangent = Vector3.Normalize(r1 - r0);
            uint b = (uint)(verts.Count / FloatsPerVertex);
            Push(verts, apex, faceN, new Vector2(0.5f, 1f), tangent);
            Push(verts, r0, faceN, new Vector2(0f, 0f), tangent);
            Push(verts, r1, faceN, new Vector2(1f, 0f), tangent);
            idx.Add(b); idx.Add(b + 1); idx.Add(b + 2);
        }
        // Base cap (facing −Z).
        var backN = new Vector3(0f, 0f, -1f);
        var backTan = new Vector3(1f, 0f, 0f);
        uint center = (uint)(verts.Count / FloatsPerVertex);
        Push(verts, new Vector3(0f, 0f, baseZ), backN, new Vector2(0.5f, 0.5f), backTan);
        for (int j = 0; j <= segments; j++)
        {
            float a = (float)j / segments * MathF.PI * 2f;
            Push(verts, new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, baseZ), backN,
                new Vector2(0.5f + 0.5f * MathF.Cos(a), 0.5f + 0.5f * MathF.Sin(a)), backTan);
        }
        for (uint j = 0; j < segments; j++)
        {
            idx.Add(center); idx.Add(center + j + 2); idx.Add(center + j + 1);
        }
        return (verts.ToArray(), idx.ToArray());
    }

    /// <summary>A torus ring for the warp gate: the ring lies in the XY plane (its hole faces ±Z), tube
    /// radius <paramref name="minor"/> about a ring of radius <paramref name="major"/>.</summary>
    public static (float[] Vertices, uint[] Indices) Torus(float major = 1f, float minor = 0.28f, int ringDiv = 40, int tubeDiv = 18)
    {
        var verts = new List<float>();
        var idx = new List<uint>();

        for (int i = 0; i <= ringDiv; i++)
        {
            float u = (float)i / ringDiv * MathF.PI * 2f;
            float cu = MathF.Cos(u), su = MathF.Sin(u);
            var ringCenter = new Vector3(cu * major, su * major, 0f);
            for (int j = 0; j <= tubeDiv; j++)
            {
                float vv = (float)j / tubeDiv * MathF.PI * 2f;
                float cv = MathF.Cos(vv), sv = MathF.Sin(vv);
                // Tube point: offset out along the ring radius (in XY) and along Z.
                var n = new Vector3(cu * cv, su * cv, sv);
                var pos = ringCenter + new Vector3(cu * cv * minor, su * cv * minor, sv * minor);
                var tangent = new Vector3(-su, cu, 0f); // along the ring
                Push(verts, pos, Vector3.Normalize(n), new Vector2((float)i / ringDiv, (float)j / tubeDiv), tangent);
            }
        }

        int stride = tubeDiv + 1;
        for (int i = 0; i < ringDiv; i++)
        {
            for (int j = 0; j < tubeDiv; j++)
            {
                uint p0 = (uint)(i * stride + j);
                uint p1 = p0 + 1;
                uint p2 = (uint)((i + 1) * stride + j);
                uint p3 = p2 + 1;
                idx.Add(p0); idx.Add(p2); idx.Add(p1);
                idx.Add(p1); idx.Add(p2); idx.Add(p3);
            }
        }
        return (verts.ToArray(), idx.ToArray());
    }

    private static void Push(List<float> verts, Vector3 pos, Vector3 normal, Vector2 uv, Vector3 tangent)
    {
        verts.Add(pos.X); verts.Add(pos.Y); verts.Add(pos.Z);
        verts.Add(normal.X); verts.Add(normal.Y); verts.Add(normal.Z);
        verts.Add(uv.X); verts.Add(uv.Y);
        verts.Add(tangent.X); verts.Add(tangent.Y); verts.Add(tangent.Z); verts.Add(1f);
    }
}
