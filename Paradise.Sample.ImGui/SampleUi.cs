using System;
using System.Collections.Generic;
using System.Numerics;
using Paradise.ECS;
using Paradise.Sample.Pool;
using Paradise.Sample.Ui;

namespace Paradise.Sample.ImGui;

/// <summary>
/// The MVVM COMPOSITION ROOT of the ImGui sample. It owns the snapshot <see cref="SimulationRunner"/>,
/// racks a small self-contained pool set (a cue ball + object balls, each with a single pocket a short
/// slide away), and wires the ViewModel/View split (<see cref="PoolViewModel"/> ↔ <see cref="PoolView"/>).
/// <see cref="Tick"/> advances the sim one fixed step and <see cref="Draw"/> renders the View; the host
/// runner calls them back-to-back on one thread so the immediate-mode View reads coherent snapshot state.
///
/// Zero-gravity planar balls (no table/floor): a break/nudge slides an object ball across XZ until its
/// center crosses a pocket mouth, which MovementSystem captures the instant it happens (planar, no floor
/// needed) → a BallPocketed event → the ScoreSystem reactor increments Score one frame later.
/// </summary>
public sealed class SampleUi : IDisposable
{
    private readonly SimulationRunner _runner;
    private readonly PoolViewModel _vm;
    private readonly PoolView _view = new();

    public SampleUi()
    {
        _runner = new SimulationRunner(new NoNavMesh());

        // Planar tuning: solver gravity stays on the authored default axis, but scoring is a planar
        // (XZ) pocket capture, so balls score by sliding regardless — and the ImGui sample renders no
        // 3D balls, only the projected numbers.
        PhysicsTuning tuning = new(0.01f, 0.02f, 1.2f, gravity: Vector3.Zero);
        const float radius = 0.12f;

        Entity cue = _runner.SpawnBall(new Vector3(0f, 0f, 0.6f), Quaternion.Identity, radius, tuning: tuning);

        // Rack: four object balls ahead in −Z, each with ONE pocket a short slide further −Z.
        Vector3[] rack =
        {
            new(0.00f, 0f, -0.40f),
            new(-0.26f, 0f, -0.80f),
            new(0.26f, 0f, -0.80f),
            new(0.00f, 0f, -1.20f),
        };
        var objectBalls = new List<Entity>(rack.Length);
        foreach (Vector3 p in rack)
        {
            Vector2 pocketCenter = new(p.X, p.Z - 1.0f);
            PocketConfig pocket = new()
            {
                PocketCount = 1,
                ParkPosition = new Vector3(pocketCenter.X, p.Y, pocketCenter.Y + 0.75f),
                RespawnPosition = p,
                IsCue = 0,
            };
            pocket.Pockets[0] = new Vector4(pocketCenter.X, pocketCenter.Y, 0.09f, 0f); // r² = 0.3²
            objectBalls.Add(_runner.SpawnBall(p, Quaternion.Identity, radius, pocket: pocket, tuning: tuning));
        }

        _vm = new PoolViewModel(_runner, cue, objectBalls);
    }

    /// <summary>Advance the sim one fixed step (called on the UI pump thread, before the View draws).</summary>
    public void Tick() => _runner.TickOnce();

    /// <summary>Render the View over the ViewModel (immediate-mode, sim thread).</summary>
    public void Draw() => _view.Draw(_vm);

    public void Dispose() => _runner.Dispose();
}
