using System;
using Paradise.Sample.Odyssey;
using Paradise.Sample.Ui;

namespace Paradise.Sample.ImGui;

/// <summary>
/// The MVVM COMPOSITION ROOT of the "Space Odyssey" sample. It owns the single-threaded snapshot
/// <see cref="OdysseyRunner"/> and wires the ViewModel/View split (<see cref="OdysseyViewModel"/> ↔
/// <see cref="OdysseyView"/>). <see cref="Tick"/> advances the sim one fixed step and <see cref="Draw"/>
/// renders the View; the host runner calls them back-to-back on one thread so the immediate-mode View
/// reads state coherent with the tick that produced it. Mirrors the pool sample's <c>PoolSampleUi</c>.
/// </summary>
public sealed class OdysseyUi : IDisposable
{
    private readonly OdysseyRunner _runner = new();
    private readonly OdysseyViewModel _vm;
    private readonly OdysseyView _view = new();

    public OdysseyUi() => _vm = new OdysseyViewModel(_runner);

    /// <summary>Advance the sim one fixed step (called on the UI pump thread, before the View draws).</summary>
    public void Tick() => _runner.TickOnce();

    /// <summary>Render the View over the ViewModel (immediate-mode, sim thread).</summary>
    public void Draw() => _view.Draw(_vm);

    public void Dispose() => _runner.Dispose();
}
