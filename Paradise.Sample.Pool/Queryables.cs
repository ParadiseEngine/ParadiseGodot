namespace Paradise.Sample.Pool;

// Queryables compose the single-variable components (Components.cs) into the exact per-variable
// read/write sets each system touches — per-variable single-writer ownership, the point of the split.
// Config/inline-buffer bags are claimed read-only (they are authored at spawn, never mutated here).

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
/// Player/NPC agents for the unified <see cref="MovementSystem"/>: transform + steering state
/// (writable) and movement config (read-only, snapshot-bound). The waypoint buffer is read-only
/// (the planner fills it). Agents also act as the kinematic pushers for ball dynamics.
/// </summary>
[Queryable]
[With<Position>]
[With<Rotation>]
[With<NavCursor>]
[With<HasPath>]
[With<MoveIntent>]
[With<NavWaypoints>]
[With<NavAgent>(IsReadOnly = true)]
[With<CharacterBody>(IsReadOnly = true)]
[With<SimulationContext>(IsReadOnly = true)]
[With<PhysicsWorldRef>(IsReadOnly = true)]
public readonly ref partial struct Agents;

/// <summary>Flipbook sprite clocks for <see cref="SpriteAnimationSystem"/>: the mutated time/frame
/// singles plus the read-only layout config.</summary>
[Queryable]
[With<SpriteTime>]
[With<SpriteFrame>]
[With<SpriteConfig>(IsReadOnly = true)]
[With<SimulationContext>(IsReadOnly = true)]
public readonly ref partial struct SpriteAnimations;

/// <summary>Particle emitters for <see cref="ParticleSystem"/>: the writable runtime-state bag plus
/// read-only config + pose. The transform is read-only (snapshot-bound): emitters are placed scenery,
/// a one-tick-stale pose only trails moving emitters by 1/60 s.</summary>
[Queryable]
[With<ParticleState>]
[With<ParticleConfig>(IsReadOnly = true)]
[With<Position>(IsReadOnly = true)]
[With<Rotation>(IsReadOnly = true)]
[With<SimulationContext>(IsReadOnly = true)]
public readonly ref partial struct ParticleEmitters;

/// <summary>Dynamic physics balls for the unified <see cref="MovementSystem"/>: transform + dynamics
/// state + pool bookkeeping (writable), with per-ball physics constants + pocket set + tuning read-only.</summary>
[Queryable]
[With<Position>]
[With<Rotation>]
[With<Velocity>]
[With<AngularVelocity>]
[With<BallGlow>]
[With<BallSunk>]
[With<BallSinking>]
[With<SinkTargetY>]
[With<BallPhysicsConfig>(IsReadOnly = true)]
[With<PocketConfig>(IsReadOnly = true)]
[With<BallId>(IsReadOnly = true)]
[With<SimulationContext>(IsReadOnly = true)]
[With<PhysicsWorldRef>(IsReadOnly = true)]
[With<PhysicsTuning>(IsReadOnly = true)]
public readonly ref partial struct Balls;

/// <summary>The single score entity for the <see cref="ScoreSystem"/> reactor demo. A normal iterated
/// queryable (there is exactly one <see cref="Score"/> entity — not a Singleton): the reactor loops its
/// one row and folds in last frame's <c>SystemEvents</c>. Sole writer of <see cref="Score"/>.</summary>
[Queryable]
[With<Score>]
public readonly ref partial struct Scores;
