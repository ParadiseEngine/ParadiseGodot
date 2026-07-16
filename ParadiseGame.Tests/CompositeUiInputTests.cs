using System.Collections.Generic;
using ParadiseGame.Ui;

namespace ParadiseGame.Tests;

/// <summary>The UI input fan-out: pointer downs/ups stop at the first consumer in
/// registration order (earlier = higher priority); moves and resizes broadcast to all;
/// ticks fan out unconditionally.</summary>
public class CompositeUiInputTests
{
    private sealed class RecordingInput(bool consumes) : IUiInput
    {
        public readonly List<UiEvent> Events = new();
        public readonly List<double> Ticks = new();
        public bool Handle(in UiEvent uiEvent) { Events.Add(uiEvent); return consumes; }
        public void Tick(double simTimeSeconds) => Ticks.Add(simTimeSeconds);
    }

    [Test]
    public async Task pointer_down_stops_at_the_first_consumer()
    {
        var first = new RecordingInput(consumes: true);
        var second = new RecordingInput(consumes: true);
        var composite = new CompositeUiInput(first, second);

        var consumed = composite.Handle(UiEvent.PointerUp(1, 2, UiPointerButton.Left));
        await Assert.That(consumed).IsTrue();
        await Assert.That(first.Events.Count).IsEqualTo(1);
        await Assert.That(second.Events.Count).IsEqualTo(0); // never reached
    }

    [Test]
    public async Task pointer_down_falls_through_non_consumers()
    {
        var first = new RecordingInput(consumes: false);
        var second = new RecordingInput(consumes: false);
        var composite = new CompositeUiInput(first, second);

        var consumed = composite.Handle(UiEvent.PointerUp(1, 2, UiPointerButton.Left));
        await Assert.That(consumed).IsFalse();
        await Assert.That(first.Events.Count).IsEqualTo(1);
        await Assert.That(second.Events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task moves_and_resizes_broadcast_to_all_even_when_consumed()
    {
        var first = new RecordingInput(consumes: true);
        var second = new RecordingInput(consumes: false);
        var composite = new CompositeUiInput(first, second);

        var consumed = composite.Handle(UiEvent.PointerMove(3, 4));
        composite.Handle(UiEvent.Resize(800, 600));

        await Assert.That(consumed).IsTrue(); // any consumer marks the move consumed…
        await Assert.That(first.Events.Count).IsEqualTo(2);
        await Assert.That(second.Events.Count).IsEqualTo(2); // …but everyone still saw it
    }

    [Test]
    public async Task ticks_fan_out_to_every_input()
    {
        var first = new RecordingInput(consumes: true);
        var second = new RecordingInput(consumes: true);
        var composite = new CompositeUiInput(first, second);

        composite.Tick(0.5);
        composite.Tick(1.0);

        await Assert.That(first.Ticks).IsEquivalentTo(new[] { 0.5, 1.0 });
        await Assert.That(second.Ticks).IsEquivalentTo(new[] { 0.5, 1.0 });
    }
}
