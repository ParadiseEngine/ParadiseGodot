using System;
using System.Numerics;

namespace Paradise.Sample.ImGui
{
    // Inside the namespace so `ImGui` resolves to ImGuiNET.ImGui, not to this namespace's own
    // trailing `ImGui` segment (a compilation-unit using cannot win that lookup).
    using ImGuiNET;
    using Paradise.Sample.Ui;

    /// <summary>The MVVM VIEW for the "Space Odyssey" sample — a thin ImGui renderer over a single
    /// <see cref="OdysseyViewModel"/>. It holds ONLY presentation state (a fixed starfield laid out
    /// once from a seeded RNG so the stars stay put across frames); all sim state is read through the
    /// ViewModel and every button forwards to a ViewModel command. Runs immediate-mode ON THE SIM
    /// THREAD (registered via ImGuiUiCore.AddDraw), the same contract PoolView relies on.</summary>
    public sealed class OdysseyView
    {
        private const int StarCount = 140;

        // Stable per-star layout: fractional position (0..1 of the display), radius, base colour and a
        // twinkle phase. Seeded ONCE so the field is identical every frame — only the twinkle animates.
        private readonly Vector2[] _starFrac = new Vector2[StarCount];
        private readonly float[] _starRadius = new float[StarCount];
        private readonly uint[] _starColor = new uint[StarCount];
        private readonly float[] _starPhase = new float[StarCount];

        public OdysseyView()
        {
            var rng = new Random(12345);
            for (var i = 0; i < StarCount; i++)
            {
                _starFrac[i] = new Vector2((float)rng.NextDouble(), (float)rng.NextDouble());
                _starRadius[i] = 0.6f + (float)rng.NextDouble() * 1.4f;
                // Cool greys/blues: full-ish blue, greenish mid, dimmer red — a starlight tint.
                var b = (byte)(180 + rng.Next(0, 76));
                var g = (byte)(150 + rng.Next(0, 80));
                var r = (byte)(140 + rng.Next(0, 70));
                _starColor[i] = Pack(r, g, b, 255);
                _starPhase[i] = (float)(rng.NextDouble() * Math.PI * 2.0);
            }
        }

        public void Draw(OdysseyViewModel vm)
        {
            DrawStarfield();

            ImGui.SetNextWindowPos(new Vector2(40, 40), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(460, 420), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Star Voyager"))
            {
                ImGui.Text($"Sector {vm.Sector}");
                ImGui.Separator();

                // Warp-charge gauge.
                ImGui.Text("Warp charge");
                ImGui.ProgressBar(
                    (float)Math.Clamp(vm.EnergyFraction, 0.0, 1.0),
                    new Vector2(-1, 0),
                    $"{vm.Energy:F0} / {vm.EnergyToJump:F0}");

                // Hull gauge.
                ImGui.Text("Hull integrity");
                ImGui.ProgressBar(
                    (float)Math.Clamp(vm.HullFraction, 0.0, 1.0),
                    new Vector2(-1, 0),
                    $"{vm.Hull:F0} / {vm.FullHull:F0}");

                ImGui.Separator();
                ImGui.Text($"Credits:     {vm.CreditBalance}");
                ImGui.Text($"Distance:    {vm.Distance:F1} ly");
                ImGui.Text($"Jump chance: {vm.JumpChance * 100f:F0} %%");

                ImGui.Separator();
                if (ImGui.Button(vm.IsCharging ? "Charging…" : "Charge"))
                {
                    vm.ToggleCharging();
                }
                ImGui.SameLine();

                var canWarp = vm.EnergyFraction >= 1.0 && !vm.IsDestroyed;
                if (!canWarp)
                {
                    ImGui.BeginDisabled();
                }
                if (ImGui.Button("Warp Jump"))
                {
                    vm.Warp();
                }
                if (!canWarp)
                {
                    ImGui.EndDisabled();
                }
                ImGui.SameLine();
                if (ImGui.Button("New Voyage"))
                {
                    vm.NewVoyage();
                }

                if (vm.IsDestroyed)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.25f, 0.25f, 1f));
                    ImGui.TextWrapped("HULL BREACH — press New Voyage");
                    ImGui.PopStyleColor();
                }

                ImGui.Separator();
                ImGui.Text("Ship's log");
                if (ImGui.BeginChild("shiplog", new Vector2(0, 120), ImGuiChildFlags.Borders))
                {
                    var log = vm.Log;
                    for (var i = 0; i < log.Count; i++)
                    {
                        ImGui.TextWrapped(log[i]);
                    }
                    // Keep the newest line in view (log appends oldest → newest).
                    if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f)
                    {
                        ImGui.SetScrollHereY(1f);
                    }
                }
                ImGui.EndChild();
            }
            ImGui.End();
        }

        /// <summary>Paint the fixed starfield into the background draw list (behind every window), a
        /// subtle per-star twinkle riding on <c>ImGui.GetTime()</c>.</summary>
        private void DrawStarfield()
        {
            var drawList = ImGui.GetBackgroundDrawList();
            Vector2 display = ImGui.GetIO().DisplaySize;
            var t = (float)ImGui.GetTime();
            for (var i = 0; i < StarCount; i++)
            {
                var pos = new Vector2(_starFrac[i].X * display.X, _starFrac[i].Y * display.Y);
                // Twinkle: brightness oscillates in [~0.45 .. 1.0].
                var twinkle = 0.725f + 0.275f * MathF.Sin(t * 2f + _starPhase[i]);
                var color = ScaleAlpha(_starColor[i], twinkle);
                drawList.AddCircleFilled(pos, _starRadius[i], color);
            }
        }

        private static uint Pack(byte r, byte g, byte b, byte a) =>
            (uint)a << 24 | (uint)b << 16 | (uint)g << 8 | r; // ImGui packs ABGR (IM_COL32)

        private static uint ScaleAlpha(uint color, float scale)
        {
            var a = (byte)Math.Clamp((color >> 24) * scale, 0f, 255f);
            return (color & 0x00FFFFFFu) | ((uint)a << 24);
        }
    }
}
