using System;
using System.Numerics;

namespace Paradise.Sample.Pool;

/// <summary>
/// Deterministic CPU particle step — the sole writer of <see cref="ParticleState"/>. Each
/// fixed tick, per emitter: (1) integrate live particles (gravity, drag, world-space advance,
/// aging — expired particles free their slot), then (2) spawn <c>EmitRate × dt</c> new
/// particles (fractional remainder carried) into free slots, launched in a cone around the
/// emitter's +Y axis using the emitter's OWN seeded xorshift stream. Slots are stable for a
/// particle's whole life and the RNG is per-emitter, so the stream is independent of emitter
/// iteration order and identical in both hosts. Rendering (quad vs cube, sheet, tint) is
/// presentation data — renderers read the particle pool straight out of world snapshots.
/// </summary>
public ref partial struct ParticleSystem : IWorldSystem
{
    public ParticleEmitters.Segments Emitters;

    public void Execute()
    {
        for (int i = 0; i < Emitters.Length; i++)
        {
            float dt = Emitters.SimulationContext[i].DeltaSeconds;
            if (dt <= 0f)
            {
                continue;
            }

            ref ParticleState state = ref Emitters.ParticleState[i];
            ref readonly ParticleConfig cfg = ref Emitters.ParticleConfig[i];
            Integrate(ref state, in cfg, dt);
            Spawn(ref state, in cfg, Emitters.Position[i].Value, Emitters.Rotation[i].Value, dt);
        }
    }

    private static void Integrate(ref ParticleState state, in ParticleConfig cfg, float dt)
    {
        for (int slot = 0; slot < cfg.Capacity; slot++)
        {
            ref Particle particle = ref state.Particles[slot];
            if (particle.Lifetime <= 0f)
            {
                continue;
            }

            particle.Age += dt;
            if (particle.Age >= particle.Lifetime)
            {
                particle.Lifetime = 0f; // free the slot; renderers see it disappear
                continue;
            }

            particle.Velocity.Y += cfg.Gravity * dt;
            if (cfg.Drag > 0f)
            {
                particle.Velocity *= MathF.Max(0f, 1f - cfg.Drag * dt);
            }
            particle.Position += particle.Velocity * dt;
        }
    }

    private static void Spawn(ref ParticleState state, in ParticleConfig cfg, Vector3 origin, Quaternion rotation, float dt)
    {
        state.SpawnCarry += cfg.EmitRate * dt;
        int toSpawn = (int)state.SpawnCarry;
        if (toSpawn <= 0)
        {
            return;
        }
        state.SpawnCarry -= toSpawn;

        int slot = 0;
        for (int n = 0; n < toSpawn; n++)
        {
            while (slot < cfg.Capacity && state.Particles[slot].Lifetime > 0f)
            {
                slot++;
            }
            if (slot >= cfg.Capacity)
            {
                return; // pool full — drop the overflow (rate stays bounded by capacity/lifetime)
            }

            state.Particles[slot] = new Particle
            {
                Position = origin,
                Velocity = Vector3.Transform(
                    ConeDirection(ref state.RngState, cfg.SpreadRadians), rotation)
                    * cfg.InitialSpeed,
                Age = 0f,
                Lifetime = cfg.LifetimeSeconds,
            };
            slot++;
        }
    }

    /// <summary>Uniform direction inside a cone of <paramref name="halfAngle"/> around +Y
    /// (uniform over the spherical cap, so tight cones don't bunch at the rim).</summary>
    private static Vector3 ConeDirection(ref uint rng, float halfAngle)
    {
        float cosTheta = 1f - NextFloat(ref rng) * (1f - MathF.Cos(halfAngle));
        float sinTheta = MathF.Sqrt(MathF.Max(0f, 1f - cosTheta * cosTheta));
        float phi = NextFloat(ref rng) * (2f * MathF.PI);
        return new Vector3(sinTheta * MathF.Cos(phi), cosTheta, sinTheta * MathF.Sin(phi));
    }

    // xorshift32 — tiny, unmanaged, identical on every host; state must never be 0.
    private static uint NextUInt(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    /// <summary>Uniform [0, 1) from the top 24 bits (exact in float).</summary>
    private static float NextFloat(ref uint state) => (NextUInt(ref state) >> 8) * (1f / 16777216f);
}
