using Paradise.Ui;
using Paradise.Sample.Ui;

namespace Paradise.Sample.Ui.Tests;

/// <summary>The shared ImGui core, fully headless (cimgui is CPU-only): the sim half builds
/// frames into snapshots, the render-half acquire API hands out the newest one exactly once
/// as "new", and the triple buffer recycles instead of growing. One process-global ImGui
/// context — tests share a single core, serialized.</summary>
[NotInParallel]
public class ImGuiUiCoreTests
{
    // One context per process (ImGui.CreateContext is global) — lazily shared across tests.
    private static ImGuiUiCore? s_core;

    private static ImGuiUiCore Core()
    {
        if (s_core is null)
        {
            try
            {
                s_core = new ImGuiUiCore(640, 360);
            }
            catch (Exception e) when (e is DllNotFoundException or TypeInitializationException)
            {
                Skip.Test($"cimgui native not loadable on this host: {e.Message}");
            }
        }
        return s_core!;
    }

    [Test]
    public async Task font_atlas_is_captured_at_construction()
    {
        var core = Core();
        await Assert.That(core.FontWidth).IsGreaterThan(0u);
        await Assert.That(core.FontHeight).IsGreaterThan(0u);
        await Assert.That(core.FontPixels.Length)
            .IsEqualTo((int)(core.FontWidth * core.FontHeight * 4));
    }

    [Test]
    public async Task tick_produces_a_snapshot_and_acquire_reports_newness_once()
    {
        var core = Core();

        core.Input.Tick(1.0 / 60.0);
        var first = core.AcquireSnapshotForRender(out var isNew);
        await Assert.That(first).IsNotNull();
        await Assert.That(isNew).IsTrue();
        await Assert.That(first!.DisplaySize.X).IsEqualTo(640f);

        // No new sim tick: same snapshot, not new — retained hosts skip the rebuild.
        var again = core.AcquireSnapshotForRender(out isNew);
        await Assert.That(ReferenceEquals(again, first)).IsTrue();
        await Assert.That(isNew).IsFalse();

        // Next tick: a fresh snapshot replaces it.
        core.Input.Tick(2.0 / 60.0);
        var second = core.AcquireSnapshotForRender(out isNew);
        await Assert.That(isNew).IsTrue();
        await Assert.That(ReferenceEquals(second, first)).IsFalse();
    }

    [Test]
    public async Task triple_buffer_recycles_snapshots_instead_of_growing()
    {
        var core = Core();

        // Steady-state alternation: after warmup, the same snapshot instances round-trip
        // through the free pool — ticks without an acquire recycle _latest in place.
        core.Input.Tick(10.0);
        var a = core.AcquireSnapshotForRender(out _);
        core.Input.Tick(10.1);
        var b = core.AcquireSnapshotForRender(out _);
        core.Input.Tick(10.2);
        var c = core.AcquireSnapshotForRender(out _);
        core.Input.Tick(10.3);
        var d = core.AcquireSnapshotForRender(out _);

        await Assert.That(ReferenceEquals(a, b)).IsFalse();
        // The pool holds ≤3 instances: by the 3rd/4th acquire we must be reusing one.
        await Assert.That(ReferenceEquals(c, a) || ReferenceEquals(d, a) || ReferenceEquals(d, b)).IsTrue();
    }

    [Test]
    public async Task resize_updates_display_size_without_consuming()
    {
        var core = Core();
        var consumed = core.Input.Handle(UiEvent.Resize(800, 600));
        await Assert.That(consumed).IsFalse();

        core.Input.Tick(20.0);
        var snapshot = core.AcquireSnapshotForRender(out _);
        await Assert.That(snapshot!.DisplaySize.X).IsEqualTo(800f);
        await Assert.That(snapshot.DisplaySize.Y).IsEqualTo(600f);
        core.Input.Handle(UiEvent.Resize(640, 360)); // restore for other tests
    }
}
