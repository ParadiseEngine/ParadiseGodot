using System.Collections.Generic;
using Paradise.Sample.Ui;

namespace Paradise.Sample.Ui.Tests;

/// <summary>Drives the retained-mode <see cref="PoolOverlayViewModel"/> headlessly — the
/// DataContext of the pool Noesis overlay. Proves the projections track refreshed game state
/// and that <c>PropertyChanged</c> fires only for values that actually changed (Noesis
/// bindings are change-driven; redundant raises would re-invalidate the tree every tick).</summary>
public class PoolOverlayViewModelTests
{
    private static List<string> Record(PoolOverlayViewModel vm)
    {
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        return raised;
    }

    [Test]
    public async Task fresh_vm_shows_an_empty_tray_and_no_pause_badge()
    {
        var vm = new PoolOverlayViewModel(16);

        await Assert.That(vm.SunkCount).IsEqualTo(0);
        await Assert.That(vm.SunkFraction).IsEqualTo(0f);
        await Assert.That(vm.IsPaused).IsFalse();
    }

    [Test]
    public async Task refresh_projects_sunk_count_into_the_tray_fraction()
    {
        var vm = new PoolOverlayViewModel(16);
        var raised = Record(vm);

        vm.Refresh(sunkCount: 4, isPaused: false);

        await Assert.That(vm.SunkCount).IsEqualTo(4);
        await Assert.That(vm.SunkFraction).IsEqualTo(0.25f);
        await Assert.That(raised).IsEquivalentTo(new[]
        {
            nameof(PoolOverlayViewModel.SunkCount),
            nameof(PoolOverlayViewModel.SunkFraction),
        });
    }

    [Test]
    public async Task unchanged_refresh_raises_nothing()
    {
        var vm = new PoolOverlayViewModel(16);
        vm.Refresh(sunkCount: 4, isPaused: true);
        var raised = Record(vm);

        vm.Refresh(sunkCount: 4, isPaused: true);

        await Assert.That(raised).IsEmpty();
    }

    [Test]
    public async Task pause_flips_only_the_pause_projection()
    {
        var vm = new PoolOverlayViewModel(16);
        var raised = Record(vm);

        vm.Refresh(sunkCount: 0, isPaused: true);

        await Assert.That(vm.IsPaused).IsTrue();
        await Assert.That(raised).IsEquivalentTo(new[] { nameof(PoolOverlayViewModel.IsPaused) });
    }

    [Test]
    public async Task a_full_rack_fills_the_tray_and_zero_balls_never_divides()
    {
        var full = new PoolOverlayViewModel(16);
        full.Refresh(sunkCount: 16, isPaused: false);
        await Assert.That(full.SunkFraction).IsEqualTo(1f);

        var empty = new PoolOverlayViewModel(0);
        empty.Refresh(sunkCount: 3, isPaused: false);
        await Assert.That(empty.SunkFraction).IsEqualTo(0f);
    }
}
