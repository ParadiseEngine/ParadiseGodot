using System.Numerics;
using Paradise.Export.Data;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;

namespace Paradise.Sample.Runtime.Tests;

/// <summary>Texture rendering through the REAL runtime path and the REAL built tree: a
/// character's KTX2 — `ktx create` output from the build — must transcode, upload, and draw with
/// its texture bound. Headless GPU, skip-not-fail without an adapter. (No pixel readback exists
/// in the renderer yet; GPU validation failing the draw is the tripwire.)</summary>
public class TexturedRenderingGpuTests
{
    [Test]
    public async Task slot_override_inherits_the_glb_textures_and_keeps_its_factors()
    {
        var level = LevelLoader.Load(BuiltFixture.Scene("sample"));
        // Ball2 references the shared textured sphere_ball (the gradient) and carries its own
        // color-only slot override — the canonical "textured GLB + differing tint" case.
        var ball2 = level.Scene.Entities.First(e => e.Name == "Ball2");
        var cooked = level.Meshes[ball2.Get<RenderableComponentData>()!.Mesh!];
        // Three "materials" meet here and they are not the same thing: the entity's SLOTS (its
        // overrides), the GLB's OWN material (through its seed prefab), and the loaded documents
        // both name (level.Materials).
        var glb = level.Materials[cooked.Materials[0]!];
        var overrideDocument = level.Materials[ball2.Get<MaterialsComponentData>()!.Slots[0]!];
        // Precondition: the GLB material this slot maps to is genuinely textured.
        await Assert.That(SceneAssembler.HasAnyTexture(glb)).IsTrue();

        var material = SceneAssembler.BuildSlotOverrideMaterial(overrideDocument, glb);

        // The textures come from the GLB's material…
        await Assert.That(material.BaseColorTexture).IsEqualTo(glb.BaseColorTexture);
        await Assert.That(SceneAssembler.HasAnyTexture(material)).IsTrue();
        // …while the color factors are the override's (Ball2's tint, NOT the GLB's base).
        await Assert.That(material.BaseColorFactor.R).IsEqualTo(overrideDocument.BaseColorFactor.R);
        await Assert.That(material.BaseColorFactor.G).IsEqualTo(overrideDocument.BaseColorFactor.G);
        await Assert.That(material.BaseColorFactor.R != glb.BaseColorFactor.R || material.BaseColorFactor.B != glb.BaseColorFactor.B).IsTrue();
    }

    [Test]
    public async Task built_ktx2_character_texture_uploads_and_renders()
    {
        var level = LevelLoader.Load(BuiltFixture.Scene("sample"));
        // Dragon: a single-material, single-image model.
        var meshField = level.Scene.Entities.First(e => e.Name == "Dragon").Get<RenderableComponentData>()!.Mesh!;
        var cooked = level.Meshes[meshField];
        var glb = level.Materials[cooked.Materials[0]!];

        // The fixture really is textured: the build wrote a KTX2 for its base colour.
        await Assert.That(glb.BaseColorTexture).IsNotNull();
        var (material, images) = SceneAssembler.ToRendererMaterial(glb, level);
        await Assert.That(images.Length).IsEqualTo(1);
        await Assert.That(material.BaseColorImage).IsEqualTo(0);

        WebGpuRenderer renderer;
        try
        {
            renderer = WebGpuRenderer.CreateHeadless(64, 64);
        }
        catch (AdapterUnavailableException ex)
        {
            Skip.Test($"No WebGPU adapter available on this host: {ex.Message}");
            return;
        }
        catch (DllNotFoundException ex)
        {
            Skip.Test($"WebGPU native library not loadable on this host: {ex.Message}");
            return;
        }

        using (renderer)
        using (var pbr = new PbrRenderer(renderer, 64, 64))
        {
            var texturesBefore = pbr.Materials.TextureCount;
            var materialId = pbr.Materials.AddMaterial(in material, images);
            // The KTX2 transcoded and uploaded as a distinct GPU texture (not a shared default).
            await Assert.That(pbr.Materials.TextureCount).IsGreaterThan(texturesBefore);

            var (primitives, _) = SceneAssembler.UploadDraws(pbr, cooked.Blob, materialId);
            await Assert.That(primitives.Length).IsGreaterThan(0);

            var scene = new PbrScene
            {
                Camera = new PbrCamera
                {
                    View = PbrMath.LookAt(new Vector3(0f, 0.5f, 2f), Vector3.Zero, Vector3.UnitY),
                    Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                    Position = new Vector3(0f, 0.5f, 2f),
                },
            };
            scene.Lights.Add(new PbrLight
            {
                Type = PbrLightType.Directional,
                Direction = Vector3.Normalize(new Vector3(0.3f, 1f, 0.4f)),
                Intensity = 1.5f,
            });
            scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh(primitives) });

            // Three sampled draws — a broken texture/bind-group/transcode fails GPU validation.
            for (var i = 0; i < 3; i++)
            {
                scene.Instances[0].Model = Matrix4x4.CreateRotationY(i * 0.5f);
                pbr.RenderFrame(scene);
            }
        }
    }
}
