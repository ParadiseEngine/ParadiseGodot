using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;
using Paradise.Sample.ImGui;
using Paradise.Sample.Pool.Ui;
using static SDL.SDL3;
using SDL;

namespace Paradise.Sample.Runtime;

/// <summary>Standalone host for the "Space Odyssey" sample (<c>--game odyssey</c>). The UI runs on
/// <see cref="ImGuiSampleRunner"/>'s 60 Hz sim thread; this host only pumps SDL events into the
/// runner's queue and renders an empty <see cref="PbrScene"/> (a near-black space clear) composited
/// with the ImGui overlay through the renderer's OverlayPass. The ImGui frame itself is produced on
/// the SIM thread; the render half here just replays the latest snapshot. Same sample core as the
/// Godot bridge (<c>scenes/odyssey.tscn</c>).</summary>
internal static class OdysseyHost
{
    private const int Width = 1280;
    private const int Height = 720;

    public static int Run(int? headlessFrames, string? screenshotPath)
    {
        using var runner = new ImGuiSampleRunner();
        return headlessFrames is { } frames
            ? RunHeadless(runner, frames, screenshotPath)
            : RunWindowed(runner);
    }

    private static int RunHeadless(ImGuiSampleRunner runner, int frameCount, string? screenshotPath)
    {
        SDL_SetHint(SDL_HINT_VIDEO_DRIVER, "dummy"u8);
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            Console.Error.WriteLine($"SDL_Init failed: {SDL_GetError()}");
            return 1;
        }

        try
        {
            using var renderer = WebGpuRenderer.CreateHeadless(Width, Height);
            var (_, pbr, scene, sample) = Compose(runner, renderer);
            using var _sample = sample;
            runner.Start();
            for (var i = 0; i < frameCount; i++)
            {
                // CI smoke: poke the UI mid-run so the pointer path executes on the sim thread.
                if (i == frameCount / 2)
                {
                    runner.EnqueueUiEvent(UiEvent.PointerMove(Width / 2f, Height / 2f));
                }
                Thread.Sleep(8); // give the sim thread real time to produce UI snapshots
                pbr.RenderFrame(scene);
            }
            if (runner.ThreadException is { } ex)
            {
                Console.Error.WriteLine($"[Odyssey] sim thread faulted: {ex}");
                return 1;
            }
            Console.WriteLine($"[Odyssey] Headless: rendered {frameCount} frames, sim tick {runner.Frame}.");

            if (screenshotPath is not null)
            {
                var pixels = renderer.ReadbackColor(out var w, out var h);
                Program.WriteBmp(screenshotPath, pixels, w, h);
                Console.WriteLine($"[Odyssey] Screenshot: {screenshotPath} ({w}x{h}).");
            }
            return 0;
        }
        finally
        {
            SDL_Quit();
        }
    }

    private static unsafe int RunWindowed(ImGuiSampleRunner runner)
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
            window = SDL_CreateWindow("Paradise — Space Odyssey", Width, Height, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (window == null)
            {
                Console.Error.WriteLine($"SDL_CreateWindow failed: {SDL_GetError()}");
                return 1;
            }

            var surfaceDesc = SdlSurface.BuildDescriptor(window, out metalView);
            renderer = new WebGpuRenderer(in surfaceDesc);
            var (_, pbr, scene, sample) = Compose(runner, renderer, surfaceDesc.Width, surfaceDesc.Height);
            using var _sample = sample;

            int logicalW, logicalH;
            SDL_GetWindowSize(window, &logicalW, &logicalH);
            var uiScale = logicalW > 0 ? surfaceDesc.Width / (float)logicalW : 1f;
            SDL_StartTextInput(window); // parity with the ImGui pump; harmless for this sample

            runner.Start();

            var quit = false;
            SDL_Event ev;
            while (!quit)
            {
                if (runner.ThreadException is { } ex)
                {
                    Console.Error.WriteLine($"[Odyssey] sim thread faulted: {ex}");
                    return 1;
                }

                while (SDL_PollEvent(&ev))
                {
                    var type = (SDL_EventType)ev.type;
                    switch (type)
                    {
                        case SDL_EventType.SDL_EVENT_QUIT:
                        case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                            quit = true;
                            break;
                        case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                        case SDL_EventType.SDL_EVENT_WINDOW_RESIZED:
                        {
                            int pw, ph, lw, lh;
                            SDL_GetWindowSizeInPixels(window, &pw, &ph);
                            SDL_GetWindowSize(window, &lw, &lh);
                            if (pw > 0 && ph > 0)
                            {
                                renderer.Resize((uint)pw, (uint)ph);
                                pbr.Resize((uint)pw, (uint)ph);
                                uiScale = lw > 0 ? pw / (float)lw : 1f;
                                runner.EnqueueUiEvent(UiEvent.Resize(pw, ph));
                            }
                            break;
                        }
                        case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                            runner.EnqueueUiEvent(UiEvent.PointerMove(ev.motion.x * uiScale, ev.motion.y * uiScale));
                            break;
                        case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN when ToButton(ev.button.button) is { } downButton:
                            runner.EnqueueUiEvent(new UiEvent(
                                UiEventKind.PointerDown, ev.button.x * uiScale, ev.button.y * uiScale,
                                downButton, default, default, false));
                            break;
                        case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP when ToButton(ev.button.button) is { } upButton:
                            runner.EnqueueUiEvent(UiEvent.PointerUp(ev.button.x * uiScale, ev.button.y * uiScale, upButton));
                            break;
                        case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                            runner.EnqueueUiEvent(UiEvent.Scroll(ev.wheel.x, ev.wheel.y));
                            break;
                    }
                }

                pbr.RenderFrame(scene);
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

    private static (ImGuiUi ImGui, PbrRenderer Pbr, PbrScene Scene, OdysseyUi Sample) Compose(
        ImGuiSampleRunner runner, WebGpuRenderer renderer, uint width = Width, uint height = Height)
    {
        var imgui = new ImGuiUi(width, height);
        var sample = new OdysseyUi();      // MVVM composition root: owns the snapshot sim
        runner.OnSimTick = sample.Tick;    // step the sim on the sim thread each frame
        imgui.AddDraw(sample.Draw);        // the thin ImGui View over the ViewModel
        imgui.Attach(renderer);
        renderer.OverlayPass = imgui.RecordOverlay;
        runner.UiInput = imgui.Input; // the sim thread owns the ImGui frame from here on

        var pbr = new PbrRenderer(renderer, width, height);
        var scene = new PbrScene
        {
            Camera = new PbrCamera
            {
                View = System.Numerics.Matrix4x4.Identity,
                Projection = System.Numerics.Matrix4x4.Identity,
            },
            // Deep-space near-black; the ImGui starfield draws over it.
            ClearColor = new ColorRgba(0.01f, 0.01f, 0.03f, 1f),
        };
        return (imgui, pbr, scene, sample);
    }

    private static UiPointerButton? ToButton(byte sdlButton) => sdlButton switch
    {
        (byte)SDL_BUTTON_LEFT => UiPointerButton.Left,
        (byte)SDL_BUTTON_RIGHT => UiPointerButton.Right,
        (byte)SDL_BUTTON_MIDDLE => UiPointerButton.Middle,
        _ => null,
    };
}
