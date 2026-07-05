namespace ParadiseGame;

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

/// <summary>Dynamic physics balls for the unified <see cref="MovementSystem"/>.</summary>
[Queryable]
[With<LocalTransform>]
[With<DynamicBody>]
[With<SimulationContext>(IsReadOnly = true)]
[With<PhysicsWorldRef>(IsReadOnly = true)]
public readonly ref partial struct Balls;
