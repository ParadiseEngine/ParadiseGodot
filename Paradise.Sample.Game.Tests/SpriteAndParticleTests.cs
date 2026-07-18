using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Sample.Game;
using Paradise.Sample.Game.Navigation.Detour;

namespace Paradise.Sample.Game.Tests;

/// <summary>The flipbook clock and the deterministic particle emitter, driven synchronously
/// through TickOnce: frames advance/loop/hold, particles spawn at the authored rate, age out,
/// reuse slots, respect the pool capacity, integrate gravity — and the whole stream is a pure
/// function of the seed (two runners with the same seed agree bit-for-bit).</summary>
public class SpriteAndParticleTests
{
    private static DetourNavigationMesh FlatGround()
    {
        var verts = new List<Vector3> { new(0, 0, 0), new(30, 0, 0), new(30, 0, 30), new(0, 0, 30) };
        var tris = new List<int> { 0, 2, 1, 0, 3, 2 };
        return new DetourNavigationMesh(verts, tris);
    }

    private static SpriteAnimation SpriteOf(SimulationRunner runner, Entity entity)
    {
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        return latest.GetComponent<SpriteAnimation>(entity);
    }

    private static ParticleEmitter EmitterOf(SimulationRunner runner, Entity entity)
    {
        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        return latest.GetComponent<ParticleEmitter>(entity);
    }

    private static int LiveCount(in ParticleEmitter emitter)
    {
        var live = 0;
        for (var slot = 0; slot < emitter.Capacity; slot++)
        {
            if (emitter.Particles[slot].Lifetime > 0f) live++;
        }
        return live;
    }

    // ---- sprite animation ----

    [Test]
    public async Task sprite_frame_advances_at_the_authored_fps_and_loops()
    {
        using var runner = new SimulationRunner(FlatGround());
        // 10 fps, 4 frames → a frame every 6 ticks, a full cycle every 24.
        var sprite = runner.SpawnSpriteAnimation(new Vector3(5, 1, 5), Quaternion.Identity,
            fps: 10f, frameCount: 4, loop: true);

        for (var i = 0; i < 7; i++) runner.TickOnce(); // t ≈ 0.1167 s → frame 1
        await Assert.That(SpriteOf(runner, sprite).Frame).IsEqualTo(1);

        for (var i = 0; i < 24; i++) runner.TickOnce(); // one full cycle later → still frame 1
        await Assert.That(SpriteOf(runner, sprite).Frame).IsEqualTo(1);
    }

    [Test]
    public async Task non_looping_sprite_holds_the_last_frame()
    {
        using var runner = new SimulationRunner(FlatGround());
        var sprite = runner.SpawnSpriteAnimation(new Vector3(5, 1, 5), Quaternion.Identity,
            fps: 30f, frameCount: 3, loop: false);

        for (var i = 0; i < 60; i++) runner.TickOnce(); // 1 s — way past the 0.1 s clip
        await Assert.That(SpriteOf(runner, sprite).Frame).IsEqualTo(2);
    }

    [Test]
    public async Task frame_sampling_rule_wraps_and_clamps()
    {
        await Assert.That(SpriteAnimationSystem.SampleFrame(0f, 10f, 4, loop: true)).IsEqualTo(0);
        await Assert.That(SpriteAnimationSystem.SampleFrame(0.35f, 10f, 4, loop: true)).IsEqualTo(3);
        await Assert.That(SpriteAnimationSystem.SampleFrame(0.45f, 10f, 4, loop: true)).IsEqualTo(0);
        await Assert.That(SpriteAnimationSystem.SampleFrame(9f, 10f, 4, loop: false)).IsEqualTo(3);
        await Assert.That(SpriteAnimationSystem.SampleFrame(9f, 10f, 1, loop: true)).IsEqualTo(0);
    }

    // ---- particle emitter ----

    private static ParticleEmitter Fountain(float rate = 60f, float lifetime = 0.5f, int capacity = 64, uint seed = 7)
        => new(emitRate: rate, lifetimeSeconds: lifetime, initialSpeed: 2f,
            spreadRadians: 0.4f, gravity: -9.8f, drag: 0f, capacity: capacity, seed: seed);

    [Test]
    public async Task emitter_spawns_at_the_authored_rate_and_particles_age_out()
    {
        using var runner = new SimulationRunner(FlatGround());
        // 60/s at 60 Hz = 1 per tick; 0.5 s lifetime → steady state ≈ 30 live.
        var emitter = runner.SpawnParticleEmitter(new Vector3(5, 1, 5), Quaternion.Identity, Fountain());

        for (var i = 0; i < 10; i++) runner.TickOnce();
        await Assert.That(LiveCount(EmitterOf(runner, emitter))).IsEqualTo(10);

        for (var i = 0; i < 80; i++) runner.TickOnce(); // past one lifetime: births balance deaths
        var live = LiveCount(EmitterOf(runner, emitter));
        await Assert.That(live).IsGreaterThanOrEqualTo(28);
        await Assert.That(live).IsLessThanOrEqualTo(31);
    }

    [Test]
    public async Task full_pool_drops_overflow_and_reuses_freed_slots()
    {
        using var runner = new SimulationRunner(FlatGround());
        // 600/s into 8 slots with 0.2 s lifetime: pool saturates, then churns via freed slots.
        var emitter = runner.SpawnParticleEmitter(new Vector3(5, 1, 5), Quaternion.Identity,
            Fountain(rate: 600f, lifetime: 0.2f, capacity: 8));

        for (var i = 0; i < 60; i++)
        {
            runner.TickOnce();
            var state = EmitterOf(runner, emitter);
            await Assert.That(LiveCount(state)).IsLessThanOrEqualTo(8);
        }
        await Assert.That(LiveCount(EmitterOf(runner, emitter))).IsEqualTo(8);
    }

    [Test]
    public async Task gravity_pulls_particles_down()
    {
        using var runner = new SimulationRunner(FlatGround());
        // Straight-up cone (zero spread), no drag: velocity.Y must strictly fall tick over tick.
        var emitter = runner.SpawnParticleEmitter(new Vector3(5, 1, 5), Quaternion.Identity,
            new ParticleEmitter(emitRate: 1f, lifetimeSeconds: 5f, initialSpeed: 1f,
                spreadRadians: 0f, gravity: -9.8f, drag: 0f, capacity: 4, seed: 3));

        // 1/s at 60 Hz: the fractional carry crosses 1.0 on tick ~61 (float rounding), so run
        // a little past a full second before reading the first particle.
        for (var i = 0; i < 65; i++) runner.TickOnce();
        var young = EmitterOf(runner, emitter).Particles[0];
        for (var i = 0; i < 30; i++) runner.TickOnce();
        var older = EmitterOf(runner, emitter).Particles[0];

        await Assert.That(young.Lifetime).IsGreaterThan(0f);
        await Assert.That(older.Velocity.Y).IsLessThan(young.Velocity.Y);
    }

    [Test]
    public async Task same_seed_same_particles_different_seed_different_particles()
    {
        static ParticleEmitter AfterTicks(uint seed, int ticks, out int liveCount)
        {
            using var runner = new SimulationRunner(FlatGround());
            var entity = runner.SpawnParticleEmitter(
                new Vector3(5, 1, 5), Quaternion.Identity, Fountain(seed: seed));
            for (var i = 0; i < ticks; i++) runner.TickOnce();
            runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
            var emitter = latest.GetComponent<ParticleEmitter>(entity);
            liveCount = LiveCount(emitter);
            return emitter;
        }

        var a = AfterTicks(seed: 42, ticks: 45, out var liveA);
        var b = AfterTicks(seed: 42, ticks: 45, out var liveB);
        var c = AfterTicks(seed: 43, ticks: 45, out _);

        await Assert.That(liveA).IsEqualTo(liveB);
        var anyDiffersFromC = false;
        for (var slot = 0; slot < a.Capacity; slot++)
        {
            // Bit-for-bit: the stream is a pure function of the seed and the tick count.
            await Assert.That(a.Particles[slot].Position).IsEqualTo(b.Particles[slot].Position);
            await Assert.That(a.Particles[slot].Velocity).IsEqualTo(b.Particles[slot].Velocity);
            await Assert.That(a.Particles[slot].Age).IsEqualTo(b.Particles[slot].Age);
            anyDiffersFromC |= a.Particles[slot].Velocity != c.Particles[slot].Velocity;
        }
        await Assert.That(anyDiffersFromC).IsTrue();
    }

    [Test]
    public async Task emitter_rotation_tilts_the_emission_cone()
    {
        using var runner = new SimulationRunner(FlatGround());
        // Emitter rotated 90° about Z: +Y maps to −X, so particles fly toward −X, not up.
        var tilted = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        var emitter = runner.SpawnParticleEmitter(new Vector3(5, 1, 5), tilted,
            new ParticleEmitter(emitRate: 60f, lifetimeSeconds: 2f, initialSpeed: 2f,
                spreadRadians: 0f, gravity: 0f, drag: 0f, capacity: 8, seed: 5));

        for (var i = 0; i < 10; i++) runner.TickOnce();
        var state = EmitterOf(runner, emitter);
        await Assert.That(state.Particles[0].Lifetime).IsGreaterThan(0f);
        await Assert.That(state.Particles[0].Velocity.X).IsLessThan(-1.9f);
        await Assert.That(MathF.Abs(state.Particles[0].Velocity.Y)).IsLessThan(1e-4f);
    }
}
