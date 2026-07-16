using System.Linq;
using Paradise.Assets.Gltf;
using ParadiseExport.Data;

namespace ParadiseRuntime.Tests;

/// <summary>The fancy pool-ball materials and the slot-override inheritance rule they rely on:
/// a solid override (no albedo texture) must fully REPLACE the surface — Godot parity — instead
/// of silently re-tinting the shared sphere_ball.glb gradient; and the committed pool.json carries
/// the metal / emissive / transmission factors the balls were authored with.</summary>
public class FancyMaterialTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "scenes", "pool.json")))
        {
            dir = dir.Parent!;
        }
        return dir!.FullName;
    }

    private static RuntimeLevel LoadPool() =>
        LevelLoader.Load(Path.Combine(RepoRoot(), "data", "scenes", "pool.json"));

    private static LevelMaterialData BallMaterial(RuntimeLevel level, string stableId)
    {
        var entity = level.Level.Entities.First(e => e.StableId == stableId);
        return level.Materials[entity.Materials[0]!];
    }

    [Test]
    public async Task solid_override_replaces_the_surface_instead_of_inheriting_the_glb_texture()
    {
        // The shared sphere_ball.glb material is genuinely textured (the gradient), sourced via
        // sample.json's Ball2 (which keeps the textured slot override).
        var sample = LevelLoader.Load(Path.Combine(RepoRoot(), "data", "scenes", "sample.json"));
        var ball2 = sample.Level.Entities.First(e => e.Id == "Ball2");
        GltfAsset asset = sample.MeshAssets[ball2.Components.Renderable!.Mesh!];
        var texturedGlb = asset.Materials[0];
        await Assert.That(SceneAssembler.HasAnyTexture(in texturedGlb)).IsTrue();

        // An override that references a texture inherits it (glTF factor × texture — Godot parity).
        var tinted = new LevelMaterialData { BaseColorTexture = "data/primitives/sphere_ball_albedo.png" };
        await Assert.That(SceneAssembler.ShouldInheritTextures(tinted, in texturedGlb)).IsTrue();

        // An override with NO texture fully replaces the surface (solid) — must not pull the GLB
        // gradient back in. This is the fix that lets the fancy pool balls render solid.
        var solid = new LevelMaterialData { BaseColorTexture = null };
        await Assert.That(SceneAssembler.ShouldInheritTextures(solid, in texturedGlb)).IsFalse();
    }

    [Test]
    public async Task fancy_ball_materials_are_solid_with_metal_emissive_and_transmission()
    {
        var level = LoadPool();

        // Every pool ball dropped the shared gradient — solid colour, no inherited texture.
        foreach (var id in new[] { "CueBall", "Ball1", "Ball8", "Ball9" })
        {
            await Assert.That(BallMaterial(level, id).BaseColorTexture).IsNull();
        }

        // Ball1 = gold: fully metallic.
        await Assert.That(BallMaterial(level, "Ball1").MetallicFactor).IsGreaterThan(0.9f);
        // Ball8 = obsidian: near-black dielectric, very low roughness.
        var obsidian = BallMaterial(level, "Ball8");
        await Assert.That(obsidian.MetallicFactor).IsLessThan(0.1f);
        await Assert.That(obsidian.RoughnessFactor).IsLessThan(0.1f);
        // Ball9 = ice: the transmission signal survived the exporter (metadata → contract → JSON).
        await Assert.That(BallMaterial(level, "Ball9").TransmissionFactor).IsGreaterThan(0f);
        // Ball5 = lava: emissive is non-black.
        var lava = BallMaterial(level, "Ball5");
        await Assert.That(lava.EmissiveFactor.R + lava.EmissiveFactor.G + lava.EmissiveFactor.B).IsGreaterThan(0f);
    }
}
