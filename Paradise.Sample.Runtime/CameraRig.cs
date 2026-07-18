using System.Numerics;
using Paradise.Rendering.Pbr;
using Paradise.Export.Data;

namespace Paradise.Sample.Runtime;

/// <summary>Fixed scene camera from the exported <see cref="CameraData"/> (position + Godot
/// YXZ Euler degrees). The contract carries no projection-mode/FOV field (schema v3
/// candidate) — Godot's Camera3D default is perspective 75° vertical, so that is the default
/// here; <c>--ortho</c> switches to <see cref="CameraData.OrthographicSize"/>, <c>--fov N</c>
/// overrides the angle.</summary>
public sealed class CameraRig
{
    private readonly Vector3 _position;
    private readonly Matrix4x4 _rotation;
    private readonly float _orthographicSize;
    private readonly bool _useOrthographic;
    private readonly float _fovDegrees;

    public CameraRig(CameraData? camera, bool useOrthographic, float fovDegrees)
    {
        _position = camera?.Position ?? new Vector3(0f, 10f, 10f);
        var euler = camera?.Rotation ?? new Vector3(-45f, 0f, 0f);
        // Godot composes Euler YXZ (column-vector Y·X·Z) = row-vector Z then X then Y.
        _rotation =
            Matrix4x4.CreateRotationZ(Radians(euler.Z)) *
            Matrix4x4.CreateRotationX(Radians(euler.X)) *
            Matrix4x4.CreateRotationY(Radians(euler.Y));
        _orthographicSize = Math.Max(0.01f, camera?.OrthographicSize ?? 5f);
        _useOrthographic = useOrthographic;
        _fovDegrees = fovDegrees;
    }

    public Vector3 Position => _position;

    /// <summary>Camera forward in world space (−Z rotated by the camera basis).</summary>
    public Vector3 Forward => Vector3.TransformNormal(-Vector3.UnitZ, _rotation);

    /// <summary>Planar (XZ) camera-relative basis for WASD — forward flattened to the ground
    /// plane, right perpendicular. Falls back to world axes for a straight-down camera.</summary>
    public (Vector3 Forward, Vector3 Right) PlanarBasis()
    {
        var forward = Forward;
        forward.Y = 0f;
        var length = forward.Length();
        forward = length > 1e-4f ? forward / length : -Vector3.UnitZ;
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        // RH: Up × Forward = LEFT for -Z forward; negate to get right.
        return (forward, -right);
    }

    public PbrCamera Build(float aspect)
    {
        var world = _rotation * Matrix4x4.CreateTranslation(_position);
        Matrix4x4.Invert(world, out var view);
        var projection = _useOrthographic
            ? PbrMath.Orthographic(_orthographicSize, aspect, 0.05f, 500f)
            : PbrMath.Perspective(Radians(_fovDegrees), aspect, 0.05f, 500f);
        return new PbrCamera { View = view, Projection = projection, Position = _position };
    }

    private static float Radians(float degrees) => degrees * (MathF.PI / 180f);
}
