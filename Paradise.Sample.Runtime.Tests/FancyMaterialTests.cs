using System.Linq;
using Paradise.Export.Data;

namespace Paradise.Sample.Runtime.Tests;

/// <summary>The fancy pool-ball materials and the slot-override inheritance rule they rely on:
/// a solid override (no albedo texture) must fully REPLACE the surface — Godot parity — instead
/// of silently re-tinting the shared sphere_ball gradient; and the built pool scene carries the
/// metal / emissive / transmission factors the balls were authored with.</summary>
public class FancyMaterialTests
{
    private static RuntimeLevel LoadPool() => LevelLoader.Load(BuiltFixture.Scene("pool"));

    private static LevelMaterialData BallMaterial(RuntimeLevel level, string stableId)
    {
        var entity = level.Scene.Entities.First(e => e.Name == stableId);
        return level.Materials[entity.Get<MaterialsComponentData>()!.Slots[0]!];
    }

    [Test]
    public async Task solid_override_replaces_the_surface_instead_of_inheriting_the_glb_texture()
    {
        // The shared sphere_ball's own material is genuinely textured (the gradient), reached
        // through sample's Ball2 — which keeps a textured slot override — and its mesh's seed prefab.
        var sample = LevelLoader.Load(BuiltFixture.Scene("sample"));
        var ball2 = sample.Scene.Entities.First(e => e.Name == "Ball2");
        var cooked = sample.Meshes[ball2.Get<RenderableComponentData>()!.Mesh!];
        var texturedGlb = sample.Materials[cooked.Materials[0]!];
        await Assert.That(SceneAssembler.HasAnyTexture(texturedGlb)).IsTrue();

        // An override that references a texture inherits it (glTF factor × texture — Godot parity).
        var tinted = new LevelMaterialData { BaseColorTexture = "models/primitives/sphere_ball_albedo.ktx2" };
        await Assert.That(SceneAssembler.ShouldInheritTextures(tinted, texturedGlb)).IsTrue();

        // An override with NO texture fully replaces the surface (solid) — must not pull the GLB
        // gradient back in. This is the fix that lets the fancy pool balls render solid.
        var solid = new LevelMaterialData { BaseColorTexture = null };
        await Assert.That(SceneAssembler.ShouldInheritTextures(solid, texturedGlb)).IsFalse();
    }

    [Test]
    public async Task procedural_ball_materials_carry_kind_hdr_emissive_and_transmission()
    {
        var level = LoadPool();

        // Every pool ball dropped the shared gradient — solid colour, no inherited texture.
        foreach (var id in new[] { "CueBall", "Ball1", "Ball8", "Ball9" })
        {
            await Assert.That(BallMaterial(level, id).BaseColorTexture).IsNull();
        }

        // Procedural material kinds survived authoring → document → build, and map to shader recipe ids.
        await Assert.That(BallMaterial(level, "Ball1").MaterialKind).IsEqualTo("molten_metal");
        await Assert.That(SceneAssembler.ProceduralKindId(BallMaterial(level, "Ball1").MaterialKind)).IsEqualTo(5);
        await Assert.That(BallMaterial(level, "Ball8").MaterialKind).IsEqualTo("obsidian");
        await Assert.That(BallMaterial(level, "Ball9").MaterialKind).IsEqualTo("ice");
        // Ball9 = ice: the transmission signal survived.
        await Assert.That(BallMaterial(level, "Ball9").TransmissionFactor).IsGreaterThan(0f);
        // Ball5 = lava: HDR emissive strength > 1 (drives the recipe's glow, blooms past white).
        var lava = BallMaterial(level, "Ball5");
        await Assert.That(lava.MaterialKind).IsEqualTo("lava");
        await Assert.That(lava.EmissiveStrength).IsGreaterThan(1f);
    }
}
