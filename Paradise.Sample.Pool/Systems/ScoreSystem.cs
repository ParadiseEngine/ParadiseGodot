namespace Paradise.Sample.Pool;

/// <summary>
/// The owner-reactor for the pool <see cref="Score"/> — the demonstration of the deferred-bus reactor
/// pattern from immortal-cultivation. This system is the SOLE WRITER of <see cref="Score"/>, and it
/// writes it from NOTHING but last frame's <c>SystemEvents</c>: it never reads another entity's
/// components and no other system (nor any caller) touches <see cref="Score"/>. That is what lets
/// scoring — an inherently cross-entity concern (a BALL drops, the SCORE changes) — coexist with
/// per-entity single-writer ownership: producers merely <c>Append</c>/<c>Emit</c> an event, and this
/// owner folds them in one frame later.
///
/// Inputs (both read via the injected <see cref="SystemEventReader"/>, which binds to the immutable
/// previous-tick snapshot → one-frame-deferred delivery):
///   • <see cref="GameReset"/> — MANAGED-emitted (<c>world.Events.Emit</c>) → zero the score;
///   • <see cref="BallPocketed"/> — SYSTEM-appended by <see cref="MovementSystem"/> → +1 per object
///     ball, −1 per cue-ball scratch, clamped at 0.
/// Reset is applied first so a same-frame reset+pocket still starts the count from zero.
/// </summary>
public ref partial struct ScoreSystem : IWorldSystem
{
    /// <summary>The single score entity's writable <see cref="Score"/> (sole writer).</summary>
    public Scores.Segments Score;

    /// <summary>Last frame's deferred events (reset + pocket announcements). Binds to the read
    /// (previous-tick) snapshot — the one-frame-deferred delivery the bus guarantees.</summary>
    public SystemEventReader Inbox;

    public void Execute()
    {
        var resets = Inbox.Read<GameReset>();
        var pocketed = Inbox.Read<BallPocketed>();
        if (resets.Length == 0 && pocketed.Length == 0)
        {
            return;
        }

        for (var n = 0; n < Score.Length; n++)
        {
            if (resets.Length > 0)
            {
                Score.Score[n].Value = 0;
            }

            foreach (var e in pocketed)
            {
                Score.Score[n].Value += e.IsCue == 0 ? 1 : -1;
            }

            if (Score.Score[n].Value < 0)
            {
                Score.Score[n].Value = 0;
            }
        }
    }
}
