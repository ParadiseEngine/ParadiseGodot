namespace Paradise.Sample.Game;

/// <summary>
/// All entities carrying the shared <see cref="SimulationContext"/>. Used by
/// <see cref="SimulationTick.PrepareFrame"/> to refresh per-frame data (delta time) on every
/// simulated entity before the systems run.
/// </summary>
[Queryable]
[With<SimulationContext>]
public readonly ref partial struct SimulationContexts;

/// <summary>All entities with steering intent — zeroed each tick before the systems run.</summary>
[Queryable]
[With<MoveIntent>]
public readonly ref partial struct MoveIntents;

/// <summary>
/// Player/NPC agents for the unified <see cref="MovementSystem"/>: steering + transform state
/// (writable) and movement config (read-only, snapshot-bound under snapshot-read execution).
/// Agents also act as the kinematic pushers for ball dynamics.
/// </summary>
[Queryable]
[With<LocalTransform>]
[With<NavPath>]
[With<MoveIntent>]
[With<NavAgent>(IsReadOnly = true)]
[With<CharacterBody>(IsReadOnly = true)]
[With<SimulationContext>(IsReadOnly = true)]
[With<PhysicsWorldRef>(IsReadOnly = true)]
public readonly ref partial struct Agents;

/// <summary>Flipbook sprite clocks for <see cref="SpriteAnimationSystem"/>.</summary>
[Queryable]
[With<SpriteAnimation>]
[With<SimulationContext>(IsReadOnly = true)]
public readonly ref partial struct SpriteAnimations;

/// <summary>Particle emitters for <see cref="ParticleSystem"/>. The transform is read-only
/// (snapshot-bound): emitters are placed scenery — a one-tick-stale pose only matters for
/// moving emitters, where it trails by 1/60 s.</summary>
[Queryable]
[With<ParticleEmitter>]
[With<LocalTransform>(IsReadOnly = true)]
[With<SimulationContext>(IsReadOnly = true)]
public readonly ref partial struct ParticleEmitters;

/// <summary>Dynamic physics balls for the unified <see cref="MovementSystem"/>.</summary>
[Queryable]
[With<LocalTransform>]
[With<DynamicBody>]
[With<BallGlow>]
[With<PoolBall>]
[With<SimulationContext>(IsReadOnly = true)]
[With<PhysicsWorldRef>(IsReadOnly = true)]
[With<PhysicsTuning>(IsReadOnly = true)]
public readonly ref partial struct Balls;
