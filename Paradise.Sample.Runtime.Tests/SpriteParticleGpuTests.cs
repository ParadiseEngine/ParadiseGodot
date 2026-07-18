using System.Numerics;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;
using Paradise.Export.Data;
using Paradise.Sample.Game;
using Paradise.Sample.Game.Navigation.Detour;

namespace Paradise.Sample.Runtime.Tests;

/// <summary>The sprite-quad and particle-batch render states through the REAL dynamic-primitive
/// path: sim-driven flipbook UVs and particle pools rewrite their vertex buffers and draw in a
/// headless frame (skip-not-fail without an adapter — GPU validation failing the draw is the
/// tripwire, same policy as TexturedRenderingGpuTests).</summary>
public class SpriteParticleGpuTests
{
    private static DetourNavigationMesh FlatGround()
    {
        var verts = new List<Vector3> { new(0, 0, 0), new(30, 0, 0), new(30, 0, 30), new(0, 0, 30) };
        var tris = new List<int> { 0, 2, 1, 0, 3, 2 };
        return new DetourNavigationMesh(verts, tris);
    }

    [Test]
    public async Task sprite_quads_and_particle_batches_draw_in_a_headless_frame()
    {
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

        using var runner = new SimulationRunner(FlatGround());
        var spriteEntity = runner.SpawnSpriteAnimation(
            new Vector3(5, 1, 5), Quaternion.Identity, fps: 10f, frameCount: 4, loop: true);
        var spriteEmitter = runner.SpawnParticleEmitter(new Vector3(4, 1, 5), Quaternion.Identity,
            new ParticleEmitter(60f, 1f, 2f, 0.4f, -9.8f, 0f, capacity: 16, seed: 7));
        var voxelEmitter = runner.SpawnParticleEmitter(new Vector3(6, 1, 5), Quaternion.Identity,
            new ParticleEmitter(60f, 1f, 2f, 0.4f, -9.8f, 0f, capacity: 16, seed: 8));
        for (var i = 0; i < 30; i++) runner.TickOnce();

        using (renderer)
        using (var pbr = new PbrRenderer(renderer, 64, 64))
        {
            var spriteData = new SpriteAnimationComponentData
            {
                Columns = 2, Rows = 2, Fps = 10f, Loop = true, QuadSize = new Vector2(1f, 1f), Billboard = true,
            };
            var sprite = new SpriteQuadState(pbr, spriteData, sheetKtx2: null, spriteEntity);
            var quads = new ParticleBatchState(pbr, new ParticleEmitterComponentData
            {
                Kind = ParticleRenderKind.Sprite, MaxParticles = 16, Columns = 2, Rows = 2,
            }, sheetKtx2: null, spriteEmitter);
            var cubes = new ParticleBatchState(pbr, new ParticleEmitterComponentData
            {
                Kind = ParticleRenderKind.Voxel, MaxParticles = 16,
                Color = Color32.FromRgba(1f, 0.5f, 0.2f),
            }, sheetKtx2: null, voxelEmitter);

            await Assert.That(runner.TrySampleInterpolation(double.MaxValue, out var a, out var b, out var alpha))
                .IsTrue();
            // The sim really produced live particles for the batches to draw.
            var live = 0;
            var pool = b.GetComponent<ParticleEmitter>(spriteEmitter);
            for (var slot = 0; slot < pool.Capacity; slot++)
            {
                if (pool.Particles[slot].Lifetime > 0f) live++;
            }
            await Assert.That(live).IsGreaterThan(0);

            var cameraRight = Vector3.UnitX;
            var cameraUp = Vector3.UnitY;
            var spriteTime = b.GetComponent<SpriteAnimation>(spriteEntity).Time;
            sprite.Update(pbr, new Vector3(5, 1, 5), Quaternion.Identity, spriteTime, cameraRight, cameraUp);
            quads.Update(pbr,
                a.GetComponent<ParticleEmitter>(spriteEmitter), b.GetComponent<ParticleEmitter>(spriteEmitter),
                alpha, cameraRight, cameraUp);
            cubes.Update(pbr,
                a.GetComponent<ParticleEmitter>(voxelEmitter), b.GetComponent<ParticleEmitter>(voxelEmitter),
                alpha, cameraRight, cameraUp);

            var scene = new PbrScene
            {
                Camera = new PbrCamera
                {
                    View = PbrMath.LookAt(new Vector3(5f, 1.5f, 9f), new Vector3(5f, 1f, 5f), Vector3.UnitY),
                    Projection = PbrMath.Perspective(MathF.PI / 3f, 1f, 0.1f, 100f),
                    Position = new Vector3(5f, 1.5f, 9f),
                },
            };
            scene.Lights.Add(new PbrLight
            {
                Type = PbrLightType.Directional,
                Direction = Vector3.Normalize(new Vector3(0.3f, 1f, 0.4f)),
                Intensity = 1.5f,
            });
            scene.Instances.Add(sprite.Instance);
            scene.Instances.Add(quads.Instance);
            scene.Instances.Add(cubes.Instance);

            // Two frames: the second re-writes every dynamic buffer after a real present.
            pbr.RenderFrame(scene);
            sprite.Update(pbr, new Vector3(5, 1, 5), Quaternion.Identity, spriteTime + 0.2f, cameraRight, cameraUp);
            pbr.RenderFrame(scene);
        }
    }
}
