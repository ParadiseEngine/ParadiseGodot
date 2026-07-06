using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;

namespace ParadiseRuntime.Tests;

/// <summary>Texture rendering through the REAL runtime path and the REAL committed fixture:
/// the ball GLB's GUI-baked KTX2 (v5 `ktx create` output) must transcode, upload, and draw
/// with its texture bound — headless GPU, skip-not-fail without an adapter. (No pixel readback
/// exists in the renderer yet; GPU validation failing the draw is the tripwire.)</summary>
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
        var ball2 = level.Level.Entities.First(e => e.Id == "Ball2");
        GltfAsset asset = level.MeshAssets[ball2.Components.Renderable!.Mesh!];
        var overrideJson = level.Materials[ball2.Materials[0]!];

        var material = SceneAssembler.BuildSlotOverrideMaterial(overrideJson, in asset.Materials[0]);

        // Texture comes from the GLB…
        await Assert.That(material.BaseColorImage).IsEqualTo(asset.Materials[0].BaseColorImage);
        await Assert.That(SceneAssembler.HasAnyTexture(in material)).IsTrue();
        // …while the factors are the override's (Ball2's tint, not the GLB-baked Ball1 red).
        await Assert.That(material.BaseColorFactor.X).IsEqualTo(overrideJson.BaseColorFactor.R);
        await Assert.That(material.BaseColorFactor.Y).IsEqualTo(overrideJson.BaseColorFactor.G);
        var glbFactor = asset.Materials[0].BaseColorFactor;
        await Assert.That(material.BaseColorFactor != glbFactor).IsTrue();
    }

    [Test]
    public async Task committed_ktx2_ball_texture_uploads_and_renders()
    {
        var level = LevelLoader.Load(Path.Combine(RepoRoot(), "data", "scenes", "sample.json"));
        var ballMeshField = level.Level.Entities.First(e => e.Id == "Ball1").Components.Renderable!.Mesh!;
        GltfAsset ball = level.MeshAssets[ballMeshField];

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
