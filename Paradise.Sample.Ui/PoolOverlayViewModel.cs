using System.ComponentModel;

namespace Paradise.Sample.Ui;

/// <summary>
/// The MVVM ViewModel for the pool Noesis overlay — a pure, host-agnostic projection of the
/// pool game's display state (pocketed progress, pause) for retained-mode data binding.
/// Unlike the immediate-mode <see cref="OdysseyViewModel"/> (polled every draw), Noesis
/// bindings are change-driven, so this VM raises <see cref="PropertyChanged"/> only when a
/// value actually changes.
///
/// Threading: <see cref="Refresh"/> must run on the SIM thread — Noesis pins each view (and
/// therefore its binding updates) to the thread that created it, which in both hosts is the
/// sim thread. Hosts pass a refresh hook into the engine's <c>NoesisViewCore</c>
/// (Paradise.Ui.Noesis.Host) so it runs under the view sync lock, right before the view update
/// that consumes it — this project stays free of any Noesis dependency.
/// </summary>
public sealed class PoolOverlayViewModel : INotifyPropertyChanged
{
    private readonly int _ballCount;
    private int _sunkCount;
    private bool _isPaused;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="ballCount">Total pool balls in play (rack + cue) — the tray's full scale.</param>
    public PoolOverlayViewModel(int ballCount) => _ballCount = ballCount;

    /// <summary>Balls pocketed so far.</summary>
    public int SunkCount => _sunkCount;

    /// <summary>Pocketed fraction [0..1] the tray's fill bar binds to.</summary>
    public float SunkFraction => _ballCount > 0 ? (float)_sunkCount / _ballCount : 0f;

    /// <summary>Whether the sim is paused (rewind/re-aim mode) — shows the pause badge.</summary>
    public bool IsPaused => _isPaused;

    /// <summary>Project the latest game state into the bindings (sim thread only).</summary>
    public void Refresh(int sunkCount, bool isPaused)
    {
        if (sunkCount != _sunkCount)
        {
            _sunkCount = sunkCount;
            Raise(nameof(SunkCount));
            Raise(nameof(SunkFraction));
        }

        if (isPaused != _isPaused)
        {
            _isPaused = isPaused;
            Raise(nameof(IsPaused));
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
