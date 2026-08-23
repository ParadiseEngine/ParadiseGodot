using System.Collections.Concurrent;
using System.Diagnostics;
using Paradise.Ui;
using Paradise.Windowing;

namespace Paradise.Sample.ImGui;

/// <summary>The sim-thread half of the ImGui sample — the smallest possible version of the
/// snapshot machinery the real games use (SimulationRunner, the old CultivationRunner): a
/// 60 Hz background thread drains the host's <see cref="UiEvent"/> queue into
/// <see cref="UiInput"/> and ticks it, which runs the registered draw delegates and publishes
/// an <c>ImGuiDrawSnapshot</c> for the host's render half. Hosts never touch ImGui directly.
/// </summary>
public sealed class ImGuiSampleRunner : IDisposable
{
    private readonly ConcurrentQueue<WindowEvent> _uiEvents = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _thread;
    private volatile bool _stop;
    private Exception? _threadException;
    private long _frame;

    /// <summary>The ImGui input half (ImGuiUiCore.Input). Set by the host before
    /// <see cref="Start"/>; the sim thread owns the ImGui frame from then on.</summary>
    public IUiInput? UiInput { get; set; }

    /// <summary>Optional per-tick sim step, invoked ON THIS (sim) THREAD each frame right before the
    /// ImGui frame is built — so the immediate-mode View reads state coherent with the tick that
    /// produced it. The composition root (<c>SampleUi</c>) wires its <c>SimulationRunner.TickOnce</c>
    /// here.</summary>
    public System.Action? OnSimTick { get; set; }

    /// <summary>Non-null after the sim thread dies; hosts poll and surface it.</summary>
    public Exception? ThreadException => _threadException;

    /// <summary>Completed sim ticks — lets headless hosts verify the thread is alive.</summary>
    public long Frame => Interlocked.Read(ref _frame);

    /// <summary>Called from the host's input thread; events apply on the next sim tick.</summary>
    public void EnqueueUiEvent(in WindowEvent uiEvent) => _uiEvents.Enqueue(uiEvent);

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Runner already started.");
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "ImGuiSampleSim" };
        _thread.Start();
    }

    private void Run()
    {
        const double step = 1.0 / 60.0;
        var next = _clock.Elapsed.TotalSeconds;
        try
        {
            while (!_stop)
            {
                var now = _clock.Elapsed.TotalSeconds;
                if (now < next)
                {
                    Thread.Sleep(1);
                    continue;
                }
                next += step;
                if (next < now)
                {
                    next = now; // a long stall skips ticks instead of spiraling to catch up
                }

                if (UiInput is { } input)
                {
                    while (_uiEvents.TryDequeue(out var uiEvent))
                    {
                        input.Handle(in uiEvent);
                    }
                    // Step the sim on this thread before building the ImGui frame, so the View
                    // (run inside input.Tick's draw delegates) reads coherent snapshot state.
                    OnSimTick?.Invoke();
                    input.Tick(now);
                    Interlocked.Increment(ref _frame);
                }
            }
        }
        catch (Exception e)
        {
            _threadException = e;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join();
        _thread = null;
    }
}
