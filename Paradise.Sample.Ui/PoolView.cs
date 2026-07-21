using System.Numerics;

namespace Paradise.Sample.Ui
{
    // Inside the namespace so `ImGui` resolves to ImGuiNET.ImGui. Paradise.Sample.Ui brings ImGui.NET
    // transitively (via the Paradise.Ui.ImGui package) — the pool ImGui demo lives here, with pool,
    // rather than in the generic ImGui sample.
    using ImGuiNET;

    /// <summary>The MVVM VIEW — a thin ImGui renderer over a single <see cref="PoolViewModel"/>.
    /// It holds ONLY presentation state (the frame-time ring buffer + the demo-window toggle); all
    /// simulation state is read through the ViewModel, and every button forwards to a ViewModel
    /// command. Runs immediate-mode ON THE SIM THREAD (registered via ImGuiUiCore.AddDraw), the same
    /// contract the game panels rely on. Mirrors immortal-cultivation's Ui/Views split.</summary>
    public sealed class PoolView
    {
        private readonly float[] _frameTimes = new float[120];
        private int _frameCursor;
        private bool _showDemo;

        public void Draw(PoolViewModel vm)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            _frameTimes[_frameCursor] = io.DeltaTime * 1000f;
            _frameCursor = (_frameCursor + 1) % _frameTimes.Length;

            ImGui.SetNextWindowPos(new Vector2(40, 40), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(460, 360), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Paradise ImGui Sample — MVVM over ECS"))
            {
                ImGui.TextWrapped(
                    "This UI is an MVVM View over a snapshot ECS simulation. The ViewModel projects " +
                    "read-only state and exposes command methods; this View is thin ImGui over it. " +
                    "Score is driven by a SystemEvents reactor (ScoreSystem folds BallPocketed one " +
                    "frame later), and Reset uses a managed Emit (GameReset).");
                ImGui.Separator();

                ImGui.Text($"Score:      {vm.Score}");
                ImGui.Text($"Balls:      {vm.BallCount}");
                ImGui.Text($"Sunk:       {vm.SunkCount}");
                ImGui.Text($"Cue speed:  {vm.CueSpeed:F2} m/s");

                ImGui.Separator();
                if (ImGui.Button("Break"))
                {
                    vm.Break();
                }
                ImGui.SameLine();
                if (ImGui.Button("Nudge"))
                {
                    vm.Nudge();
                }
                ImGui.SameLine();
                if (ImGui.Button("Reset score"))
                {
                    vm.Reset();
                }

                ImGui.PlotLines(
                    "frame ms", ref _frameTimes[0], _frameTimes.Length, _frameCursor,
                    overlay_text: null, scale_min: 0f, scale_max: 33f, graph_size: new Vector2(0, 40));

                ImGui.Separator();
                ImGui.Checkbox("Show ImGui demo window", ref _showDemo);
            }
            ImGui.End();

            if (_showDemo)
            {
                ImGui.ShowDemoWindow(ref _showDemo);
            }
        }
    }
}
