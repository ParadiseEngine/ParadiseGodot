using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Sample.Pool;

namespace Paradise.Sample.Ui;

/// <summary>
/// The MVVM ViewModel for the ImGui sample — a pure, host-agnostic projection over the snapshot ECS
/// sim (<see cref="SimulationRunner"/>). It exposes READ-ONLY state (score, ball/sunk counts, cue
/// speed) computed on demand from the latest published snapshot, and COMMAND methods (break, nudge,
/// reset) that only enqueue into the runner. It has no ImGui dependency: the same ViewModel is what
/// Paradise.Sample.Ui.Tests drives headlessly. Mirrors immortal-cultivation's Ui/ViewModels split —
/// the View (PoolView, ImGui) is a thin renderer over exactly this one ViewModel.
///
/// Threading: every projection/command runs on the sim thread (the same thread that ticks the runner
/// and draws the View), so the snapshot reads are coherent with the tick that produced them.
/// </summary>
public sealed class PoolViewModel
{
    private readonly SimulationRunner _runner;
    private readonly Entity _cueBall;
    private readonly IReadOnlyList<Entity> _objectBalls;

    public PoolViewModel(SimulationRunner runner, Entity cueBall, IReadOnlyList<Entity> objectBalls)
    {
        _runner = runner;
        _cueBall = cueBall;
        _objectBalls = objectBalls;
    }

    /// <summary>The running score — written only by the ScoreSystem reactor from the SystemEvents bus.</summary>
    public int Score => _runner.Score;

    /// <summary>Total balls on the table (cue + object balls).</summary>
    public int BallCount => _objectBalls.Count + 1;

    /// <summary>Object balls whose latest snapshot marks them <see cref="BallSunk"/> (pocketed + parked).</summary>
    public int SunkCount
    {
        get
        {
            if (!_runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _))
            {
                return 0;
            }
            int sunk = 0;
            foreach (Entity e in _objectBalls)
            {
                if (latest.IsAlive(e) && latest.GetComponent<BallSunk>(e).Value != 0)
                {
                    sunk++;
                }
            }
            return sunk;
        }
    }

    /// <summary>Horizontal (XZ) speed of the cue ball from the latest snapshot, m/s.</summary>
    public float CueSpeed
    {
        get
        {
            if (!_runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _) ||
                !latest.IsAlive(_cueBall))
            {
                return 0f;
            }
            Vector3 v = latest.GetComponent<Velocity>(_cueBall).Value;
            return new Vector2(v.X, v.Z).Length();
        }
    }

    /// <summary>Fire the cue ball into the rack (a velocity impulse toward −Z).</summary>
    public void Break() => _runner.EnqueueBallImpulse(_cueBall, new Vector3(0f, 0f, -4f));

    /// <summary>Nudge the first un-sunk object ball toward its pocket (falls back to +X if the ball
    /// carries no pocket, so the command is never a no-op on a well-formed rack).</summary>
    public void Nudge()
    {
        Vector3 impulse = new(1.5f, 0f, 0f);
        if (_runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _))
        {
            foreach (Entity e in _objectBalls)
            {
                if (!latest.IsAlive(e) || latest.GetComponent<BallSunk>(e).Value != 0)
                {
                    continue;
                }
                if (latest.HasComponent<PocketConfig>(e))
                {
                    PocketConfig pocket = latest.GetComponent<PocketConfig>(e);
                    if (pocket.PocketCount > 0)
                    {
                        Vector3 pos = latest.GetComponent<Position>(e).Value;
                        Vector4 mouth = pocket.Pockets[0]; // (centerX, centerZ, r², 0)
                        Vector3 toPocket = new(mouth.X - pos.X, 0f, mouth.Y - pos.Z);
                        if (toPocket.LengthSquared() > 1e-6f)
                        {
                            impulse = Vector3.Normalize(toPocket) * 1.5f;
                        }
                    }
                }
                _runner.EnqueueBallImpulse(e, impulse);
                return;
            }
        }
        // No live object ball to nudge — nothing to do.
    }

    /// <summary>Reset the score (managed <c>GameReset</c> emit → the reactor zeroes it next tick).</summary>
    public void Reset() => _runner.RequestReset();
}
