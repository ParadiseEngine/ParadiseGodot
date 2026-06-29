#nullable enable
using System.Numerics;

namespace ParadiseExport.Core.Geometry
{
    /// <summary>
    /// PINNED CONVENTION (Phase 1 de-risk). The Paradise export contract stores transforms in
    /// the convention the original Unity tools emitted: Unity is Y-up, <b>left-handed</b>
    /// (+X right, +Y up, +Z forward). Godot is Y-up, <b>right-handed</b> (+X right, +Y up,
    /// <b>−Z</b> forward). Because the export contract is fixed (the runtime already consumes
    /// the Unity-convention data), the Godot exporter must convert Godot's right-handed values
    /// into the contract's left-handed values at export time.
    ///
    /// The conversion is a mirror of the Z axis (S = diag(1, 1, −1)):
    /// <list type="bullet">
    ///   <item>position / direction vector (x, y, z) → (x, y, −z)</item>
    ///   <item>rotation quaternion (x, y, z, w) → (−x, −y, z, w)</item>
    ///   <item>transform matrix M → S · M · S</item>
    /// </list>
    ///
    /// Validated against the real Unity export <c>data/scenes/SampleScene.json</c>: e.g. the
    /// default camera authored at Godot (0, 1, 10) looking toward −Z maps to the contract's
    /// (0, 1, −10), matching the baseline. See ParadiseExport.Core.Tests.
    /// </summary>
    public static class CoordinateConversion
    {
        /// <summary>Godot right-handed position → contract left-handed position (negate Z).</summary>
        public static Vector3 Position(Vector3 godot) => new(godot.X, godot.Y, -godot.Z);

        /// <summary>
        /// Godot right-handed direction → contract left-handed direction. A direction is a free
        /// vector, so the same Z mirror applies as for position.
        /// </summary>
        public static Vector3 Direction(Vector3 godot) => new(godot.X, godot.Y, -godot.Z);

        /// <summary>
        /// Godot right-handed rotation → contract left-handed rotation. Mirroring Z negates the
        /// X and Y quaternion components (equivalently: reversed angle about a Z-mirrored axis).
        /// </summary>
        public static Quaternion Rotation(Quaternion godot) =>
            new(-godot.X, -godot.Y, godot.Z, godot.W);

        /// <summary>
        /// Godot right-handed transform → contract left-handed transform: M → S · M · S where
        /// S = diag(1, 1, −1). Mirrors the Z row and Z column (the M13/M23/M43 and M31/M32/M34
        /// entries flip sign; M33 is mirrored twice and is unchanged).
        /// </summary>
        public static Matrix4x4 Transform(Matrix4x4 m)
        {
            // S · M · S flips exactly the entries where the Z index appears an odd number of times.
            return new Matrix4x4(
                m.M11, m.M12, -m.M13, m.M14,
                m.M21, m.M22, -m.M23, m.M24,
                -m.M31, -m.M32, m.M33, -m.M34,
                m.M41, m.M42, -m.M43, m.M44);
        }
    }
}
