using System;
using System.Numerics;
using ParadiseExport.Core.Geometry;

namespace ParadiseExport.Core.Tests;

// Pins the Godot right-handed → contract left-handed (Z-mirror) convention discovered in
// Phase 1 against the real Unity baseline.
public class CoordinateConversionTests
{
    [Test]
    public async Task position_negates_z()
    {
        // The default camera authored at Godot (0,1,10) must map to the contract's (0,1,-10),
        // matching data/scenes/SampleScene.json.
        Vector3 converted = CoordinateConversion.Position(new Vector3(0f, 1f, 10f));
        await Assert.That(converted).IsEqualTo(new Vector3(0f, 1f, -10f));
    }

    [Test]
    public async Task direction_negates_z()
    {
        Vector3 converted = CoordinateConversion.Direction(new Vector3(1f, 2f, 3f));
        await Assert.That(converted).IsEqualTo(new Vector3(1f, 2f, -3f));
    }

    [Test]
    public async Task rotation_negates_x_and_y()
    {
        // 90° about +Y in Godot: (0, sin45, 0, cos45) → (0, -sin45, 0, cos45).
        float s = MathF.Sin(MathF.PI / 4f);
        float c = MathF.Cos(MathF.PI / 4f);
        Quaternion converted = CoordinateConversion.Rotation(new Quaternion(0f, s, 0f, c));
        await Assert.That(converted).IsEqualTo(new Quaternion(0f, -s, 0f, c));
    }

    [Test]
    public async Task transform_is_an_involution()
    {
        // Mirroring twice is the identity, so the conversion must round-trip.
        Matrix4x4 m = Matrix4x4.CreateTranslation(2f, 3f, 5f) * Matrix4x4.CreateRotationY(0.7f);
        Matrix4x4 back = CoordinateConversion.Transform(CoordinateConversion.Transform(m));
        await Assert.That(back).IsEqualTo(m);
    }

    [Test]
    public async Task transform_translation_z_matches_position_negation()
    {
        // Transform must agree with Position: translation Z (M43 in System.Numerics) flips sign.
        Matrix4x4 converted = CoordinateConversion.Transform(Matrix4x4.CreateTranslation(0f, 0f, 5f));
        await Assert.That(converted.M43).IsEqualTo(-5f);
    }
}
