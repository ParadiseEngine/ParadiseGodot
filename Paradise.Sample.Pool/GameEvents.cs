namespace Paradise.Sample.Pool;

/// <summary>
/// Deferred gameplay events on the Paradise.ECS <c>SystemEvents</c> bus (engine 0.5.2) — the
/// engine-neutral fan-out channel demonstrated by this sample. An event appended by a system (or
/// emitted by managed code) THIS frame is readable by any number of consumer systems NEXT frame
/// (one-frame deferred, deterministic merge, snapshot-carried). Mirrors immortal-cultivation's
/// <c>Ecs/GameEvents.cs</c>.
/// </summary>
/// <remarks>
/// Events are plain unmanaged structs (NOT <c>[Component]</c>): they live off-entity in the world's
/// event store, so they add no per-entity archetype cost. Emit them one of two ways:
/// <list type="bullet">
///   <item>from a SYSTEM via an injected <c>SystemEventWriter</c> (<c>Events.Append(...)</c>) — see
///     <see cref="MovementSystem"/>, which announces every pocket capture;</item>
///   <item>from MANAGED code via <c>world.Events.Emit(...)</c> (engine 0.5.2) — see
///     <see cref="SimulationRunner.RequestReset"/>, which raises <see cref="GameReset"/> on the sim
///     thread before the schedule commits.</item>
/// </list>
/// Both land in the same bus and are read next frame via an injected <c>SystemEventReader</c>
/// (<c>Inbox.Read&lt;T&gt;()</c>) — the sole consumer here is the owner-reactor <see cref="ScoreSystem"/>.
/// </remarks>

/// <summary>Raised the tick a ball enters a pocket mouth — by <see cref="MovementSystem"/> at BOTH the
/// cue-scratch branch and the object-ball sink branch. <see cref="ScoreSystem"/> reads it next frame:
/// an object ball scores +1, a cue-ball scratch scores −1 (clamped at 0).</summary>
public struct BallPocketed
{
    /// <summary>The <see cref="BallId.Value"/> of the ball that dropped.</summary>
    public int BallId;

    /// <summary>1 when the dropped ball was the cue ball (a scratch), 0 for an object ball.</summary>
    public byte IsCue;
}

/// <summary>Raised by MANAGED code (<see cref="SimulationRunner.RequestReset"/> via
/// <c>world.Events.Emit</c>) to zero the score. Payload-free: the request itself is the signal.
/// <see cref="ScoreSystem"/> consumes it next frame and resets <see cref="Score"/> to 0.</summary>
public struct GameReset
{
}
