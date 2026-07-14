using System.Collections.Generic;
using System.Numerics;
using ParadiseGame;
using ParadiseGame.Navigation.Detour;
using ParadiseGame.Ui;

namespace ParadiseGame.Tests;

/// <summary>The sim-side UI contract, driven synchronously via TickOnce: queued events reach
/// <see cref="IUiInput.Handle"/> on the tick in order, UI time advances once per tick with
/// canonical sim time, consumed pointer-downs never reach game logic, and unconsumed ones with
/// a world ray fire <see cref="SimulationRunner.UiUnhandledPointerDown"/>.</summary>
public class UiInputRoutingTests
{
    private static DetourNavigationMesh FlatGround()
    {
        var verts = new List<Vector3> { new(0, 0, 0), new(20, 0, 0), new(20, 0, 20), new(0, 0, 20) };
        var tris = new List<int> { 0, 2, 1, 0, 3, 2 };
        return new DetourNavigationMesh(verts, tris);
    }

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
        using var runner = new SimulationRunner(FlatGround());
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
        using var runner = new SimulationRunner(FlatGround());
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
        using var runner = new SimulationRunner(FlatGround());
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
    public async Task world_click_routed_through_ui_moves_the_agent_same_tick()
    {
        using var runner = new SimulationRunner(FlatGround());
        var agent = runner.SpawnAgent(new Vector3(2, 0, 2), Quaternion.Identity, moveSpeed: 6f, arriveRadius: 0.25f);
        var ui = new RecordingUi { ConsumePointerDown = false };
        runner.UiInput = ui;
        // The RuntimeLoop pattern: unconsumed world clicks enqueue a move for the player.
        runner.UiUnhandledPointerDown = e => runner.EnqueueMoveTo(agent, new Vector3(18, 0, 18));

        runner.EnqueueUiEvent(UiEvent.PointerDown(5, 5, UiPointerButton.Left, new Vector3(18, 5, 18), -Vector3.UnitY));
        for (var i = 0; i < 400; i++) runner.TickOnce();

        runner.TrySampleInterpolation(double.MaxValue, out var latest, out _, out _);
        var transform = latest.GetComponent<LocalTransform>(agent);
        await Assert.That(Vector2.Distance(new Vector2(transform.Position.X, transform.Position.Z), new Vector2(18, 18)))
            .IsLessThan(0.6f);
    }
}
