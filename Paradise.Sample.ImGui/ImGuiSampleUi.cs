using System.Numerics;

namespace Paradise.Sample.ImGui
{
    // Inside the namespace so `ImGui` resolves to ImGuiNET.ImGui, not to this namespace's
    // own trailing `ImGui` segment (a compilation-unit using cannot win that lookup).
    using ImGuiNET;

    /// <summary>The sample's draw delegate — registered on ImGuiUiCore and executed ON THE SIM
    /// THREAD between NewFrame and Render, so it reads and mutates its own state freely (the
    /// immediate-mode contract the real game panels rely on). Exercises the event kinds the hosts
    /// route: pointer (button/slider), keyboard + text (input field), scroll, and per-tick delta
    /// time (frame-time plot). The stock ImGui demo window is a click away.</summary>
    public sealed class ImGuiSampleUi
    {
        private readonly ImGuiSampleRunner _runner;
        private readonly float[] _frameTimes = new float[120];
        private int _frameCursor;
        private int _clicks;
        private float _slider = 0.5f;
        private string _text = "edit me";
        private bool _showDemo;

        public ImGuiSampleUi(ImGuiSampleRunner runner) => _runner = runner;

        public void Draw()
        {
            var io = ImGui.GetIO();
            _frameTimes[_frameCursor] = io.DeltaTime * 1000f;
            _frameCursor = (_frameCursor + 1) % _frameTimes.Length;

            ImGui.SetNextWindowPos(new Vector2(40, 40), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(420, 320), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Paradise ImGui Sample"))
            {
                ImGui.TextWrapped(
                    "This UI is built on a 60 Hz sim thread and replayed by the host's render half " +
                    "from triple-buffered snapshots. The same code runs in the Godot play-mode " +
                    "bridge and the standalone SDL/WebGPU runtime.");
                ImGui.Separator();

                ImGui.Text($"Sim tick: {_runner.Frame}");
                ImGui.PlotLines(
                    "tick ms", ref _frameTimes[0], _frameTimes.Length, _frameCursor,
                    overlay_text: null, scale_min: 0f, scale_max: 33f, graph_size: new Vector2(0, 40));

                if (ImGui.Button($"Clicked {_clicks} times"))
                {
                    _clicks++;
                }
                ImGui.SliderFloat("slider", ref _slider, 0f, 1f);
                ImGui.InputText("text input", ref _text, 256);

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
