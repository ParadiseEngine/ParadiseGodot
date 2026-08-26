using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.Export.Data;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;

namespace Paradise.Sample.Runtime.Tests;

/// <summary>Texture rendering through the REAL runtime path and the REAL committed fixture:
/// a character source GLB's KTX2 (v5 `ktx create` output, embedded at data/ import) must
/// transcode, upload, and draw with its texture bound — headless GPU, skip-not-fail without an
/// adapter. (No pixel readback exists in the renderer yet; GPU validation failing the draw is the
/// tripwire.)</summary>
public class TexturedRenderingGpuTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "scenes", "sample.json")))
        {
            dir = dir.Parent!;
        }
        return dir!.FullName;
    }

    [Test]
    public async Task slot_override_inherits_the_glb_textures_and_keeps_its_factors()
    {
        var level = LevelLoader.Load(Path.Combine(RepoRoot(), "data", "scenes", "sample.json"));
        // Ball2 references the shared textured sphere_ball.glb (external gradient KTX2) and carries
        // its own color-only slot override — the canonical "textured GLB + differing tint" case.
        var ball2 = level.Level.Entities.First(e => e.Get<NameComponentData>()?.Value == "Ball2");
        // Three different "Materials" meet in these two lines and they are not the same thing:
        // the renderable's SLOTS (the entity's overrides), level.Materials (the loaded documents,
        // keyed by slot), and asset.Materials (the GLB's own). Only the first moved in v4.
        RenderableComponentData renderable = ball2.Get<RenderableComponentData>()!;
        GltfAsset asset = level.MeshAssets[renderable.Mesh!];
        var overrideJson = level.Materials[ball2.Get<MaterialsComponentData>()!.Slots[0]!];
        // Precondition: the GLB material this slot maps to is genuinely textured.
        await Assert.That(SceneAssembler.HasAnyTexture(in asset.Materials[0])).IsTrue();

        var material = SceneAssembler.BuildSlotOverrideMaterial(overrideJson, in asset.Materials[0]);

        // The texture indices come from the GLB (the slot override never carries runtime textures)…
        await Assert.That(material.BaseColorImage).IsEqualTo(asset.Materials[0].BaseColorImage);
        await Assert.That(SceneAssembler.HasAnyTexture(in material)).IsTrue();
        // …while the color factors are the override's (Ball2's blue tint, NOT the GLB's white base).
        await Assert.That(material.BaseColorFactor.X).IsEqualTo(overrideJson.BaseColorFactor.R);
        await Assert.That(material.BaseColorFactor.Y).IsEqualTo(overrideJson.BaseColorFactor.G);
        await Assert.That(material.BaseColorFactor != asset.Materials[0].BaseColorFactor).IsTrue();
    }

    [Test]
    public async Task committed_ktx2_character_texture_uploads_and_renders()
    {
        var level = LevelLoader.Load(Path.Combine(RepoRoot(), "data", "scenes", "sample.json"));
        // Dragon references a single-material, single-KTX2-image source GLB.
        var meshField = level.Level.Entities.First(e => e.Get<NameComponentData>()?.Value == "Dragon").Get<RenderableComponentData>()!.Mesh!;
        GltfAsset ball = level.MeshAssets[meshField];

        // The fixture really is textured: one KTX2 image, referenced as the base color.
        await Assert.That(ball.Images.Length).IsEqualTo(1);
        await Assert.That(ball.Materials[0].BaseColorImage).IsEqualTo(0);

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
            var materialId = pbr.Materials.AddMaterial(in ball.Materials[0], ball.Images);
            // The KTX2 transcoded and uploaded as a distinct GPU texture (not a shared default).
            await Assert.That(pbr.Materials.TextureCount).IsGreaterThan(texturesBefore);

            var primitives = new List<PbrPrimitive>();
            foreach (var instance in ball.Instances)
            {
                foreach (var primitive in ball.Meshes[instance.MeshIndex].Primitives)
                {
                    primitives.Add(pbr.UploadPrimitive(primitive.Vertices, primitive.Indices, materialId));
                }
            }
            await Assert.That(primitives.Count).IsGreaterThan(0);

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
            scene.Instances.Add(new PbrInstance { Mesh = new PbrMesh(primitives.ToArray()) });

            // Three sampled draws — a broken texture/bind-group/transcode fails GPU validation.
            for (var i = 0; i < 3; i++)
            {
                scene.Instances[0].Model = Matrix4x4.CreateRotationY(i * 0.5f);
                pbr.RenderFrame(scene);
            }
        }
    }
}
