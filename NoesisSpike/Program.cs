using Noesis;

namespace NoesisSpike;

/// <summary>Feasibility spike: drive NoesisGUI with a PURE MANAGED RenderDevice — the route
/// to a native WebGPU backend (no OpenGL anywhere). Stage 1 (--log) runs a no-GPU logging
/// device to prove the managed extension path works and to enumerate the shader/format
/// surface the XAML needs. Stage 2 (default) runs the real WebGPU device and writes the UI
/// to noesis_webgpu.bmp via texture readback.</summary>
internal static class Program
{
    private const int Width = 512;
    private const int Height = 512;

    private const string SpikeXaml = """
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              Background="Transparent">
          <Border Width="380" Height="240" CornerRadius="28"
                  HorizontalAlignment="Center" VerticalAlignment="Center">
            <Border.Background>
              <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                <GradientStop Color="#E6304FFE" Offset="0"/>
                <GradientStop Color="#E600C9A7" Offset="1"/>
              </LinearGradientBrush>
            </Border.Background>
          </Border>
          <Path Stroke="#FFFFFFFF" StrokeThickness="6"
                HorizontalAlignment="Center" VerticalAlignment="Center"
                Data="M 0,60 C 40,-40 120,160 160,60 S 280,-40 320,60"/>
          <Rectangle Width="90" Height="90" RadiusX="18" RadiusY="18" Fill="#B0FF4E6A"
                     HorizontalAlignment="Center" VerticalAlignment="Center"
                     Margin="240,180,0,0"/>
        </Grid>
        """;

    private static int Main(string[] args)
    {
        var name = Environment.GetEnvironmentVariable("NOESIS_LICENSE_NAME");
        var key = Environment.GetEnvironmentVariable("NOESIS_LICENSE_KEY");
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(key))
        {
            GUI.SetLicense(name, key);
        }
        else
        {
            Console.WriteLine("[NoesisSpike] Eval mode (no NOESIS_LICENSE_* env).");
        }

        GUI.Init();
        var root = (FrameworkElement)GUI.ParseXaml(SpikeXaml);
        var view = GUI.CreateView(root);
        view.SetFlags(RenderFlags.PPAA);
        view.SetSize(Width, Height);

        var exitCode = args.Contains("--log")
            ? RunLoggingStage(view)
            : RunWebGpuStage(view);

        GUI.Shutdown();
        return exitCode;
    }

    /// <summary>Stage 1: does Noesis call a managed RenderDevice at all?</summary>
    private static int RunLoggingStage(View view)
    {
        var device = new LoggingRenderDevice();
        view.Renderer.Init(device);

        for (var frame = 0; frame < 2; frame++)
        {
            device.Log.Add($"--- frame {frame} ---");
            view.Update(frame / 60.0);
            view.Renderer.UpdateRenderTree();
            view.Renderer.RenderOffscreen();
            view.Renderer.Render();
        }

        foreach (var line in device.Log)
        {
            Console.WriteLine("  " + line);
        }
        Console.WriteLine($"[NoesisSpike] shaders hit: {string.Join(", ", device.ShadersHit.Select(kv => $"{kv.Value}(#{kv.Key})"))}");
        var drewSomething = device.ShadersHit.Count > 0;
        Console.WriteLine(drewSomething
            ? "[NoesisSpike] MANAGED RENDER DEVICE PATH WORKS."
            : "[NoesisSpike] no DrawBatch calls — managed path did not engage.");
        view.Renderer.Shutdown();
        return drewSomething ? 0 : 2;
    }

    /// <summary>Stage 2: the same frames through a real WebGPU device + readback.</summary>
    private static int RunWebGpuStage(View view)
    {
        using var device = new WebGpuRenderDevice(Width, Height);
        view.Renderer.Init(device);

        var pixels = Array.Empty<byte>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        const int frames = 100;
        for (var i = 0; i < frames; i++)
        {
            view.Update(i / 60.0);
            view.Renderer.UpdateRenderTree();
            view.Renderer.RenderOffscreen();
            device.BeginFrame();
            view.Renderer.Render();
            pixels = device.EndFrameAndRead();
        }
        Console.WriteLine($"[NoesisSpike] {frames} frames: {clock.Elapsed.TotalMilliseconds / frames:F2} ms/frame (render + readback)");

        var covered = 0;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) covered++;
        }
        Console.WriteLine(
            $"[NoesisSpike] WebGPU frame: {covered * 100.0 / (Width * Height):F1}% covered pixels; " +
            $"skipped shaders: [{string.Join(", ", device.SkippedShaders)}]");

        WriteBmp("noesis_webgpu.bmp", pixels, Width, Height);
        Console.WriteLine("[NoesisSpike] Wrote noesis_webgpu.bmp");
        view.Renderer.Shutdown();
        return covered > 0 ? 0 : 2;
    }

    /// <summary>32-bit BGRA BMP, top-down source rows written bottom-up.</summary>
    private static void WriteBmp(string path, byte[] rgba, int width, int height)
    {
        using var w = new BinaryWriter(File.Create(path));
        var dataSize = width * height * 4;
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(14 + 40 + dataSize); w.Write(0); w.Write(14 + 40);
        w.Write(40); w.Write(width); w.Write(height);
        w.Write((short)1); w.Write((short)32);
        w.Write(0); w.Write(dataSize);
        w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        for (var y = height - 1; y >= 0; y--)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                w.Write(rgba[i + 2]); w.Write(rgba[i + 1]); w.Write(rgba[i]); w.Write(rgba[i + 3]);
            }
        }
    }
}
