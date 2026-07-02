namespace ParadiseGame.Core;

/// <summary>
/// All entities carrying the shared <see cref="SimulationContext"/>. Used by
/// <see cref="GameSimulation.Tick"/> to refresh per-frame data (delta time) on every simulated agent
/// before the systems run.
/// </summary>
[Queryable]
[With<SimulationContext>]
public readonly ref partial struct SimulationContexts;

/// <summary>All entities with steering intent — zeroed each tick before the systems run.</summary>
[Queryable]
[With<MoveIntent>]
public readonly ref partial struct MoveIntents;

/// <summary>Movable characters: steering intent integrated against the collision world.</summary>
[Queryable]
[With<LocalTransform>]
[With<MoveIntent>]
[With<CharacterBody>]
public readonly ref partial struct CharacterMovers;
