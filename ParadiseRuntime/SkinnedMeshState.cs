using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.Rendering.Pbr;

namespace ParadiseRuntime;

/// <summary>Per-entity CPU-skinning playback: advances the clip, evaluates the rig, skins every
/// skinned primitive into scratch buffers, applies the GLB node bake, and re-uploads the
/// instance's PRIVATE dynamic vertex buffers. Rigid primitives of the same model stay on the
/// shared static uploads — this exists only for entities whose contract sets InitialAnimation
/// and whose GLB actually carries the named clip.</summary>
public sealed class SkinnedMeshState
{
    /// <summary>One skinned primitive: the CPU-side source (bind vertices + joints/weights),
    /// its private dynamic GPU clone, and the GLB node bake applied after skinning (the same
    /// bake the static path burns into shared uploads).</summary>
    public sealed record SkinnedPrimitive(
        GltfPrimitive Source,
        PbrPrimitive Gpu,
        Matrix4x4 Bake,
        int SkinIndex,
        int MeshNodeIndex);

    private readonly GltfAnimationRig _rig;
    private readonly GltfAnimationData _clip;
    private readonly SkinnedPrimitive[] _primitives;
    private readonly Matrix4x4[] _palette;
    private readonly float[] _scratch;
    private readonly float _duration;
    private float _time;
    private int _paletteSkin = -1;
    private int _paletteMeshNode = -1;

    public SkinnedMeshState(GltfAsset asset, GltfAnimationData clip, SkinnedPrimitive[] primitives)
    {
        _rig = new GltfAnimationRig(asset);
        _clip = clip;
        _primitives = primitives;
        var maxJoints = 0;
        foreach (var primitive in primitives)
        {
            maxJoints = Math.Max(maxJoints, asset.Skins[primitive.SkinIndex].JointNodes.Length);
        }
        _palette = new Matrix4x4[maxJoints];
        var maxFloats = 0;
        foreach (var primitive in primitives)
        {
            maxFloats = Math.Max(maxFloats, primitive.Source.Vertices.Length);
        }
        _scratch = new float[maxFloats];
        _duration = Math.Max(clip.Duration, 1e-3f);
    }

    /// <summary>Pin the clip to a fixed time instead of advancing — deterministic captures
    /// (the parity gate seeks Godot's AnimationPlayer to the same time).</summary>
    public float? TimeOverride { get; set; }

    /// <summary>Advance the looped clip and re-upload every skinned primitive.</summary>
    public void Advance(PbrRenderer pbr, float deltaSeconds)
    {
        _time = TimeOverride ?? (_time + deltaSeconds) % _duration;
        _rig.EvaluatePose(_clip, _time);
        _paletteSkin = -1; // pose changed → palette cache invalid

        foreach (var primitive in _primitives)
        {
            if (_paletteSkin != primitive.SkinIndex || _paletteMeshNode != primitive.MeshNodeIndex)
            {
                _rig.ComputeJointPalette(primitive.SkinIndex, primitive.MeshNodeIndex, _palette);
                _paletteSkin = primitive.SkinIndex;
                _paletteMeshNode = primitive.MeshNodeIndex;
            }
            GltfAnimationRig.SkinVertices(primitive.Source, _palette, _scratch);
            BakeInPlace(_scratch, primitive.Source.Vertices.Length, primitive.Bake);
            pbr.UpdatePrimitiveVertices(primitive.Gpu, _scratch.AsSpan(0, primitive.Source.Vertices.Length));
        }
    }

    /// <summary>The static path's node bake (SceneAssembler.BakeTransform), applied in place on
    /// the skinned scratch so the dynamic buffers land in the same entity-local space as the
    /// shared static uploads.</summary>
    private static void BakeInPlace(float[] vertices, int floatCount, in Matrix4x4 transform)
    {
        if (transform.IsIdentity) return;
        Matrix4x4.Invert(transform, out var inverse);
        var normalMatrix = Matrix4x4.Transpose(inverse);
        for (var i = 0; i < floatCount; i += GltfPrimitive.FloatsPerVertex)
        {
            var position = Vector3.Transform(new Vector3(vertices[i], vertices[i + 1], vertices[i + 2]), transform);
            var normal = Vector3.Normalize(Vector3.TransformNormal(new Vector3(vertices[i + 3], vertices[i + 4], vertices[i + 5]), normalMatrix));
            var tangent = Vector3.TransformNormal(new Vector3(vertices[i + 8], vertices[i + 9], vertices[i + 10]), transform);
            vertices[i] = position.X; vertices[i + 1] = position.Y; vertices[i + 2] = position.Z;
            vertices[i + 3] = normal.X; vertices[i + 4] = normal.Y; vertices[i + 5] = normal.Z;
            var tangentLength = tangent.Length();
            if (tangentLength > 1e-6f) tangent /= tangentLength;
            vertices[i + 8] = tangent.X; vertices[i + 9] = tangent.Y; vertices[i + 10] = tangent.Z;
        }
    }
}
