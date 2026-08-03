using System.Collections.Generic;
using System.Numerics;
using Paradise.Sample.Pool;
using Paradise.Ui;

namespace Paradise.Sample.Pool.Tests;

/// <summary>The sim-side UI contract, driven synchronously via TickOnce: queued events reach
/// <see cref="IUiInput.Handle"/> on the tick in order, UI time advances once per tick with
/// canonical sim time, consumed pointer-downs never reach game logic, and unconsumed ones with
/// a world ray fire <see cref="SimulationRunner.UiUnhandledPointerDown"/>.</summary>
public class UiInputRoutingTests
{
    private sealed class RecordingUi : IUiInput
    {
        public readonly List<UiEvent> Handled = new();
        public readonly List<double> Ticks = new();
        public bool ConsumePointerDown;

        public bool Handle(in UiEvent uiEvent)
        {
            Handled.Add(uiEvent);
            return uiEvent.Kind == UiEventKind.PointerDown && ConsumePointerDown;
        }

        public void Tick(double simTimeSeconds) => Ticks.Add(simTimeSeconds);
    }

    [Test]
    public async Task events_drain_in_order_and_ui_time_is_canonical()
    {
        using var runner = new SimulationRunner();
        var ui = new RecordingUi();
        runner.UiInput = ui;

        runner.EnqueueUiEvent(UiEvent.PointerMove(10, 20));
        runner.EnqueueUiEvent(UiEvent.PointerUp(10, 20, UiPointerButton.Left));
        runner.TickOnce();
        runner.TickOnce();

        await Assert.That(ui.Handled.Count).IsEqualTo(2);
        await Assert.That(ui.Handled[0].Kind).IsEqualTo(UiEventKind.PointerMove);
        await Assert.That(ui.Handled[1].Kind).IsEqualTo(UiEventKind.PointerUp);
        await Assert.That(ui.Ticks.Count).IsEqualTo(2);
        await Assert.That(ui.Ticks[0]).IsEqualTo(SimulationRunner.FixedDeltaSeconds);
        await Assert.That(ui.Ticks[1]).IsEqualTo(2 * SimulationRunner.FixedDeltaSeconds);
    }

    [Test]
    public async Task consumed_pointer_down_never_reaches_game_logic()
    {
        using var runner = new SimulationRunner();
        var ui = new RecordingUi { ConsumePointerDown = true };
        var worldClicks = new List<UiEvent>();
        runner.UiInput = ui;
        runner.UiUnhandledPointerDown = worldClicks.Add;

        runner.EnqueueUiEvent(UiEvent.PointerDown(5, 5, UiPointerButton.Left, new Vector3(0, 5, 0), -Vector3.UnitY));
        runner.TickOnce();

        await Assert.That(ui.Handled.Count).IsEqualTo(1);
        await Assert.That(worldClicks).IsEmpty();
    }

    [Test]
    public async Task unconsumed_pointer_down_with_ray_falls_through_to_the_world()
    {
        using var runner = new SimulationRunner();
        var ui = new RecordingUi { ConsumePointerDown = false };
        var worldClicks = new List<UiEvent>();
        runner.UiInput = ui;
        runner.UiUnhandledPointerDown = worldClicks.Add;

        runner.EnqueueUiEvent(UiEvent.PointerDown(5, 5, UiPointerButton.Left, new Vector3(1, 5, 2), -Vector3.UnitY));
        // A rayless pointer-down (e.g. synthesized) must NOT fire the world hook even unconsumed.
        runner.EnqueueUiEvent(new UiEvent(UiEventKind.PointerDown, 6, 6, UiPointerButton.Left, default, default, false));
        runner.TickOnce();

        await Assert.That(worldClicks.Count).IsEqualTo(1);
        await Assert.That(worldClicks[0].WorldRayOrigin).IsEqualTo(new Vector3(1, 5, 2));
    }

    [Test]
    public async Task world_click_routed_through_ui_drives_a_world_action_same_tick()
    {
        using var runner = new SimulationRunner();
        var tuning = new PhysicsTuning(0.01f, 0.02f, 1.2f, gravity: Vector3.Zero);
        var ball = runner.SpawnBall(new Vector3(2, 0, 2), Quaternion.Identity, radius: 0.35f,
            linearDamping: 0f, angularDamping: 0f, tuning: tuning);
        var ui = new RecordingUi { ConsumePointerDown = false };
        runner.UiInput = ui;
        // The host pattern: an unconsumed world click drives a world action (here, a cue strike).
        runner.UiUnhandledPointerDown = e => runner.EnqueueBallImpulse(ball, new Vector3(3f, 0f, 0f));

        runner.EnqueueUiEvent(UiEvent.PointerDown(5, 5, UiPointerButton.Left, new Vector3(18, 5, 18), -Vector3.UnitY));
        for (var i = 0; i < 60; i++) runner.TickOnce();

        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        var pos = latest.GetComponent<Position>(ball).Value;
        await Assert.That(pos.X).IsGreaterThan(2f); // the routed impulse rolled the ball +X
    }
}
