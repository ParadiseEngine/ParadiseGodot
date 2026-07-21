using System.Diagnostics;
using System.Numerics;
using Paradise.Rendering;
using Paradise.Rendering.WebGPU;
using Paradise.Sample.Pool.Ui;
using static SDL.SDL3;
using SDL;

namespace Paradise.Sample.Runtime;

/// <summary>The standalone Paradise runtime: loads an exported scene from <c>data/</c>, runs
/// the real 60 Hz game simulation (Paradise.Sample.Pool's SimulationRunner + MovementSystem), and
/// PBR-renders interpolated snapshots in an SDL window. Left-click drags to aim/strike the cue
/// ball. <c>--headless N</c> renders N frames offscreen for CI.
///
/// Usage: Paradise.Sample.Runtime --scene data/scenes/sample.json [--headless N] [--ortho] [--fov N]
///
/// <c>--game imgui</c> instead runs the Dear ImGui integration sample (no exported scene):
/// Paradise.Sample.Runtime --game imgui [--headless N] [--screenshot path].</summary>
internal static class Program
{
    private const int InitialWidth = 1280;
    private const int InitialHeight = 720;

    private static int Main(string[] args)
    {
        string scenePath = "data/scenes/sample.json";
        string? gameName = null;
        string? uiXamlPath = null;
        var enableImGui = false;
        string? audioBanksPath = null;
        int? headlessFrames = null;
        string? screenshotPath = null;
        var orthographic = false;
        var fovDegrees = 75f; // Godot Camera3D default (vertical)
        float? animTime = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scene" when i + 1 < args.Length:
                    scenePath = args[++i];
                    break;
                case "--game" when i + 1 < args.Length:
                    gameName = args[++i];
                    break;
                case "--headless" when i + 1 < args.Length && int.TryParse(args[i + 1], out var frames):
                    headlessFrames = frames;
                    i++;
                    break;
                case "--screenshot" when i + 1 < args.Length:
                    screenshotPath = args[++i];
                    headlessFrames ??= 8; // headless implied; a few frames so the sim settles
                    break;
                case "--ortho":
                    orthographic = true;
                    break;
                case "--fov" when i + 1 < args.Length && float.TryParse(args[i + 1], out var fov):
                    fovDegrees = fov;
                    i++;
                    break;
                case "--ui" when i + 1 < args.Length:
                    uiXamlPath = args[++i];
                    break;
                case "--imgui":
                    enableImGui = true;
                    break;
                case "--audio" when i + 1 < args.Length:
                    audioBanksPath = args[++i];
                    break;
                case "--anim-time" when i + 1 < args.Length && float.TryParse(args[i + 1], out var anim):
                    // Pin skinned clips to a fixed time — deterministic captures (parity gate).
                    animTime = anim;
                    i++;
                    break;
            }
        }

        try
        {
            if (gameName is not null)
            {
                if (!string.Equals(gameName, "imgui", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"Unknown --game '{gameName}' (supported: imgui).");
                    return 1;
                }
                return ImGuiSampleHost.Run(headlessFrames, screenshotPath);
            }

            var level = LevelLoader.Load(scenePath);
            Console.WriteLine(
                $"[Paradise.Sample.Runtime] {scenePath}: {level.Level.Entities.Count} entities, " +
                $"{level.MeshAssets.Count} mesh assets, {level.Materials.Count} materials.");
            return headlessFrames is { } n
                ? RunHeadless(level, n, orthographic, fovDegrees, screenshotPath, animTime, uiXamlPath, enableImGui, audioBanksPath)
                : RunWindowed(level, orthographic, fovDegrees, animTime, uiXamlPath, enableImGui, audioBanksPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Paradise.Sample.Runtime failed: {ex}");
            return 1;
        }
    }

    private static int RunHeadless(RuntimeLevel level, int frameCount, bool orthographic, float fovDegrees, string? screenshotPath, float? animTime, string? uiXamlPath, bool enableImGui, string? audioBanksPath)
    {
        SDL_SetHint(SDL_HINT_VIDEO_DRIVER, "dummy"u8);
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            Console.Error.WriteLine($"SDL_Init failed: {SDL_GetError()}");
            return 1;
        }

        try
        {
            using var renderer = WebGpuRenderer.CreateHeadless(InitialWidth, InitialHeight);
            var ui = uiXamlPath is null ? null : new NoesisUi(uiXamlPath, InitialWidth, InitialHeight);
            var imgui = enableImGui ? new ImGuiUi(InitialWidth, InitialHeight) : null;
            var uiSystems = CollectUiSystems(imgui, ui);
            using var audio = audioBanksPath is null ? null : WwiseAudio.TryCreate(audioBanksPath);
            using var loop = new RuntimeLoop(
                level, renderer, InitialWidth, InitialHeight, orthographic, fovDegrees, animTime,
                ComposeUiInput(uiSystems), audio);
            WireOverlays(renderer, uiSystems, imgui, loop);
            loop.Start();
            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed.TotalSeconds;
            for (var i = 0; i < frameCount; i++)
            {
                // Give the sim thread real time to produce snapshots the loop can sample.
                Thread.Sleep(8);
                var now = clock.Elapsed.TotalSeconds;
                loop.RenderFrame(now - last);
                last = now;
            }
            Console.WriteLine(
                $"[Paradise.Sample.Runtime] Headless: rendered {frameCount} frames, {loop.InstanceCount} instances, " +
                $"collision={(loop.CollisionWorld is not null ? "yes" : "no")}.");

            if (screenshotPath is not null)
            {
                var pixels = renderer.ReadbackColor(out var w, out var h);
                WriteBmp(screenshotPath, pixels, w, h);
                Console.WriteLine($"[Paradise.Sample.Runtime] Screenshot: {screenshotPath} ({w}x{h}).");
            }
            return 0;
        }
        finally
        {
            SDL_Quit();
        }
    }

    /// <summary>UI systems in INPUT-PRIORITY order (first = claims pointer events first);
    /// ImGui debug panels outrank the game UI. Overlay drawing runs in the reverse order, so
    /// higher input priority also draws on top.</summary>
    private static IUiSystem[] CollectUiSystems(ImGuiUi? imgui, NoesisUi? noesis)
    {
        var systems = new List<IUiSystem>(2);
        if (imgui is not null) systems.Add(imgui);
        if (noesis is not null) systems.Add(noesis);
        return systems.ToArray();
    }

    private static Paradise.Sample.Pool.Ui.IUiInput? ComposeUiInput(IUiSystem[] systems) => systems.Length switch
    {
        0 => null,
        1 => systems[0].Input,
        _ => new CompositeUiInput(Array.ConvertAll(systems, s => s.Input)),
    };

    /// <summary>Attach every UI system and chain the overlay seam (reverse input priority:
    /// the UI that claims input first draws last, on top). Also registers the default ImGui
    /// debug panel.</summary>
    private static void WireOverlays(WebGpuRenderer renderer, IUiSystem[] systems, ImGuiUi? imgui, RuntimeLoop loop)
    {
        foreach (var system in systems)
        {
            system.Attach(renderer);
        }
        if (systems.Length > 0)
        {
            renderer.OverlayPass = (encoder, backbuffer) =>
            {
                for (var i = systems.Length - 1; i >= 0; i--)
                {
                    systems[i].RecordOverlay(encoder, backbuffer);
                }
            };
        }
        if (imgui is not null && loop.HasPoolGame)
        {
            imgui.AddDraw(loop.DrawPoolPanel);
        }
        if (imgui is not null)
        {
            // Runs on the sim thread: reading loop/sim state directly is the point.
            var showDemo = false;
            imgui.AddDraw(() =>
            {
                ImGuiNET.ImGui.Begin("Paradise");
                ImGuiNET.ImGui.Text($"instances: {loop.InstanceCount}");
                ImGuiNET.ImGui.Checkbox("ImGui demo window", ref showDemo);
                ImGuiNET.ImGui.End();
                if (showDemo) ImGuiNET.ImGui.ShowDemoWindow(ref showDemo);
            });
        }
    }

    /// <summary>Write tightly-packed top-down BGRA8 pixels as an uncompressed 32-bit BMP (bottom-up,
    /// BI_RGB — BMP stores BGRA natively). Dependency-free; convert to PNG with `sips` if needed.</summary>
    internal static void WriteBmp(string path, byte[] bgra, uint width, uint height)
    {
        const int headerSize = 54; // 14-byte file header + 40-byte info header
        var imageSize = (int)(width * height * 4);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        // BITMAPFILEHEADER
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(headerSize + imageSize); // file size
        w.Write(0);                      // reserved
        w.Write(headerSize);             // pixel data offset
        // BITMAPINFOHEADER
        w.Write(40);                     // header size
        w.Write((int)width);
        w.Write((int)height);            // positive = bottom-up
        w.Write((short)1);               // planes
        w.Write((short)32);              // bpp
        w.Write(0);                      // BI_RGB (no compression)
        w.Write(imageSize);
        w.Write(2835); w.Write(2835);    // ~72 DPI x/y
        w.Write(0); w.Write(0);          // palette
        // Pixel rows, bottom-up: source row 0 is the top, so emit from the last row upward.
        var stride = (int)(width * 4);
        for (var y = (int)height - 1; y >= 0; y--)
            w.Write(bgra, y * stride, stride);
    }

    private static unsafe int RunWindowed(RuntimeLevel level, bool orthographic, float fovDegrees, float? animTime, string? uiXamlPath, bool enableImGui, string? audioBanksPath)
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            Console.Error.WriteLine($"SDL_Init failed: {SDL_GetError()}");
            return 1;
        }

        SDL_Window* window = null;
        IntPtr metalView = IntPtr.Zero;
        WebGpuRenderer? renderer = null;
        try
        {
            window = SDL_CreateWindow("Paradise Runtime", InitialWidth, InitialHeight, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (window == null)
            {
                Console.Error.WriteLine($"SDL_CreateWindow failed: {SDL_GetError()}");
                return 1;
            }

            var surfaceDesc = SdlSurface.BuildDescriptor(window, out metalView);
            renderer = new WebGpuRenderer(in surfaceDesc);
            var ui = uiXamlPath is null ? null : new NoesisUi(uiXamlPath, surfaceDesc.Width, surfaceDesc.Height);
            var imgui = enableImGui ? new ImGuiUi(surfaceDesc.Width, surfaceDesc.Height) : null;
            var uiSystems = CollectUiSystems(imgui, ui);
            using var audio = audioBanksPath is null ? null : WwiseAudio.TryCreate(audioBanksPath);
            using var loop = new RuntimeLoop(
                level, renderer, surfaceDesc.Width, surfaceDesc.Height, orthographic, fovDegrees, animTime,
                ComposeUiInput(uiSystems), audio);
            WireOverlays(renderer, uiSystems, imgui, loop);
            int logicalW, logicalH;
            SDL_GetWindowSize(window, &logicalW, &logicalH);
            var uiScale = logicalW > 0 ? surfaceDesc.Width / (float)logicalW : 1f;
            loop.Start();
            Console.WriteLine("[Paradise.Sample.Runtime] Left-click drag to aim and strike the cue ball.");

            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed.TotalSeconds;
            var quit = false;
            SDL_Event ev;
            while (!quit)
            {
                while (SDL_PollEvent(&ev))
                {
                    var type = (SDL_EventType)ev.type;
                    if (type is SDL_EventType.SDL_EVENT_QUIT or SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
                    {
                        quit = true;
                    }
                    else if (type is SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED or SDL_EventType.SDL_EVENT_WINDOW_RESIZED)
                    {
                        var w = ev.window.data1;
                        var h = ev.window.data2;
                        if (w > 0 && h > 0)
                        {
                            renderer.Resize((uint)w, (uint)h);
                            loop.Resize((uint)w, (uint)h);
                            if (ui is not null || imgui is not null)
                            {
                                // Both resize event kinds land here, and RESIZED carries
                                // LOGICAL dims in its payload — query the authoritative pixel
                                // and logical sizes instead so the view stays pixel-sized and
                                // pointer coords keep mapping onto it.
                                int pw, ph, lw, lh;
                                SDL_GetWindowSizeInPixels(window, &pw, &ph);
                                SDL_GetWindowSize(window, &lw, &lh);
                                if (pw > 0 && ph > 0)
                                {
                                    uiScale = lw > 0 ? pw / (float)lw : 1f;
                                    loop.EnqueueUiEvent(Paradise.Sample.Pool.Ui.UiEventKind.Resize, new Vector2(pw, ph));
                                }
                            }
                        }
                    }
                    else if (type == SDL_EventType.SDL_EVENT_MOUSE_MOTION)
                    {
                        loop.UpdateAim(new Vector2(ev.motion.x, ev.motion.y) * uiScale);
                        if (ui is not null || imgui is not null)
                        {
                            loop.EnqueueUiEvent(Paradise.Sample.Pool.Ui.UiEventKind.PointerMove, new Vector2(ev.motion.x, ev.motion.y) * uiScale);
                        }
                    }
                    else if (type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP &&
                             ev.button.button == SDL_BUTTON_LEFT)
                    {
                        loop.ReleaseAim();
                        if (ui is not null || imgui is not null)
                        {
                            loop.EnqueueUiEvent(Paradise.Sample.Pool.Ui.UiEventKind.PointerUp, new Vector2(ev.button.x, ev.button.y) * uiScale);
                        }
                    }
                    else if (type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN &&
                             ev.button.button == SDL_BUTTON_LEFT)
                    {
                        // The cue ball claims the click first (start aiming); otherwise the click
                        // routes to the UI (panel clicks go through the sim).
                        if (!loop.TryBeginAim(new Vector2(ev.button.x, ev.button.y) * uiScale) &&
                            (ui is not null || imgui is not null))
                        {
                            loop.EnqueueUiEvent(Paradise.Sample.Pool.Ui.UiEventKind.PointerDown, new Vector2(ev.button.x, ev.button.y) * uiScale);
                        }
                    }
                }

                var now = clock.Elapsed.TotalSeconds;
                loop.RenderFrame(now - last);
                last = now;
            }

            return 0;
        }
        finally
        {
            renderer?.Dispose();
            if (metalView != IntPtr.Zero) SDL_Metal_DestroyView(metalView);
            if (window != null) SDL_DestroyWindow(window);
            SDL_Quit();
        }
    }
}
