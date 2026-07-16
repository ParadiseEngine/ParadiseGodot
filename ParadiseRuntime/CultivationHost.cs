using System.Diagnostics;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;
using ParadiseCultivation;
using ParadiseGame.Ui;
using static SDL.SDL3;
using SDL;

namespace ParadiseRuntime;

/// <summary>Standalone host for the Immortal Cultivation slice (<c>--game cultivation</c>).
/// The game runs on <see cref="CultivationRunner"/>'s 60 Hz sim thread (the same snapshot
/// machinery as the 3D scenes' SimulationRunner); this host only pumps SDL events into the
/// runner's UI queue and renders: an empty <see cref="PbrScene"/> (a clear) composited with
/// the ImGui overlay through the renderer's OverlayPass. The ImGui frame itself is produced
/// on the SIM thread (the runner pumps <see cref="CultivationRunner.UiInput"/> every fixed
/// step); the render half here just replays the latest snapshot.</summary>
internal static class CultivationHost
{
    private const int Width = 1600;
    private const int Height = 900;

    private static string s_glyphSource = string.Empty;

    public static int Run(string configPath, int seed, int? sizeIndex, int? headlessFrames, string? screenshotPath)
    {
        // configPath names the core file; its siblings (names/dialogue/text) compose in.
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
        string ReadPart(string file) => File.ReadAllText(Path.Combine(configDir, file));
        var config = CultivationConfig.Load(ReadPart);
        // Glyph source: every authored character across ALL config files gets a glyph.
        s_glyphSource = string.Concat(ConfigFiles.All.Select(ReadPart));
        using var runner = new CultivationRunner(config, seed, sizeIndex);
        Console.WriteLine(
            $"[Cultivation] seed {seed}: {runner.Map.Width}x{runner.Map.Height} world, " +
            $"{runner.Map.Sites.Count} sites, {runner.Npcs.Count} cultivators.");

        return headlessFrames is { } frames
            ? RunHeadless(runner, frames, screenshotPath)
            : RunWindowed(runner);
    }

    private static int RunHeadless(CultivationRunner runner, int frameCount, string? screenshotPath)
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
            var (imgui, pbr, scene) = Compose(runner, renderer);
            runner.Start();
            for (var i = 0; i < frameCount; i++)
            {
                // Headless is the CI smoke: render the new-game screen for the first third,
                // then begin the journey (playing-phase panels draw), then cultivate a month
                // so the sim thread's animated time flow + monthly settlement run in-host.
                if (i == frameCount / 3 && runner.Phase == GamePhase.NewGame)
                {
                    runner.RequestBeginJourney();
                }
                if (i == frameCount * 2 / 3 && runner.Phase == GamePhase.Playing && !runner.Busy)
                {
                    runner.RequestCultivate(1);
                }
                Thread.Sleep(8); // give the sim thread real time to produce UI snapshots
                pbr.RenderFrame(scene);
            }
            if (runner.ThreadException is { } ex)
            {
                Console.Error.WriteLine($"[Cultivation] sim thread faulted: {ex}");
                return 1;
            }
            Console.WriteLine(
                $"[Cultivation] Headless: rendered {frameCount} frames, phase={runner.Phase}, " +
                $"{CultivationRules.FormatDate(runner.Config, runner.Day)}.");

            if (screenshotPath is not null)
            {
                var pixels = renderer.ReadbackColor(out var w, out var h);
                Program.WriteBmp(screenshotPath, pixels, w, h);
                Console.WriteLine($"[Cultivation] Screenshot: {screenshotPath} ({w}x{h}).");
            }
            return 0;
        }
        finally
        {
            SDL_Quit();
        }
    }

    private static unsafe int RunWindowed(CultivationRunner runner)
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
            window = SDL_CreateWindow("Paradise — Immortal Cultivation", Width, Height, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (window == null)
            {
                Console.Error.WriteLine($"SDL_CreateWindow failed: {SDL_GetError()}");
                return 1;
            }

            var surfaceDesc = SdlSurface.BuildDescriptor(window, out metalView);
            renderer = new WebGpuRenderer(in surfaceDesc);
            var (imgui, pbr, scene) = Compose(runner, renderer, surfaceDesc.Width, surfaceDesc.Height);

            int logicalW, logicalH;
            SDL_GetWindowSize(window, &logicalW, &logicalH);
            var uiScale = logicalW > 0 ? surfaceDesc.Width / (float)logicalW : 1f;
            SDL_StartTextInput(window); // free-text NPC chat needs SDL text events

            runner.Start();

            var quit = false;
            SDL_Event ev;
            while (!quit)
            {
                if (runner.ThreadException is { } ex)
                {
                    Console.Error.WriteLine($"[Cultivation] sim thread faulted: {ex}");
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
                        case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                        {
                            var text = ev.text.GetText();
                            if (text is not null)
                            {
                                foreach (var rune in text.EnumerateRunes())
                                {
                                    runner.EnqueueUiEvent(UiEvent.Text((uint)rune.Value));
                                }
                            }
                            break;
                        }
                        case SDL_EventType.SDL_EVENT_KEY_DOWN when ToKey(ev.key.scancode) is { } keyDown:
                            runner.EnqueueUiEvent(UiEvent.KeyDown(keyDown));
                            break;
                        case SDL_EventType.SDL_EVENT_KEY_UP when ToKey(ev.key.scancode) is { } keyUp:
                            runner.EnqueueUiEvent(UiEvent.KeyUp(keyUp));
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

    private static (ImGuiUi ImGui, PbrRenderer Pbr, PbrScene Scene) Compose(
        CultivationRunner runner, WebGpuRenderer renderer, uint width = Width, uint height = Height)
    {
        // CJK-capable font (chat accepts free text): config path, or probe system fonts.
        var fontConfig = new ParadiseUi.UiFontConfig(
            string.IsNullOrWhiteSpace(runner.Config.Ui.FontPath) ? null : runner.Config.Ui.FontPath,
            runner.Config.Ui.FontSizePixels,
            s_glyphSource);
        var imgui = new ImGuiUi(width, height, fontConfig);
        var ui = new CultivationUi(runner);
        imgui.AddDraw(ui.Draw);
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
            // Ink-wash night sky behind the parchment UI.
            ClearColor = new ColorRgba(0.075f, 0.08f, 0.1f, 1f),
        };
        return (imgui, pbr, scene);
    }

    private static UiPointerButton? ToButton(byte sdlButton) => sdlButton switch
    {
        (byte)SDL_BUTTON_LEFT => UiPointerButton.Left,
        (byte)SDL_BUTTON_RIGHT => UiPointerButton.Right,
        (byte)SDL_BUTTON_MIDDLE => UiPointerButton.Middle,
        _ => null,
    };

    private static UiKey? ToKey(SDL_Scancode scancode) => scancode switch
    {
        SDL_Scancode.SDL_SCANCODE_RETURN or SDL_Scancode.SDL_SCANCODE_KP_ENTER => UiKey.Enter,
        SDL_Scancode.SDL_SCANCODE_ESCAPE => UiKey.Escape,
        SDL_Scancode.SDL_SCANCODE_BACKSPACE => UiKey.Backspace,
        SDL_Scancode.SDL_SCANCODE_DELETE => UiKey.Delete,
        SDL_Scancode.SDL_SCANCODE_TAB => UiKey.Tab,
        SDL_Scancode.SDL_SCANCODE_LEFT => UiKey.Left,
        SDL_Scancode.SDL_SCANCODE_RIGHT => UiKey.Right,
        SDL_Scancode.SDL_SCANCODE_UP => UiKey.Up,
        SDL_Scancode.SDL_SCANCODE_DOWN => UiKey.Down,
        SDL_Scancode.SDL_SCANCODE_HOME => UiKey.Home,
        SDL_Scancode.SDL_SCANCODE_END => UiKey.End,
        SDL_Scancode.SDL_SCANCODE_LCTRL or SDL_Scancode.SDL_SCANCODE_RCTRL => UiKey.Ctrl,
        SDL_Scancode.SDL_SCANCODE_LSHIFT or SDL_Scancode.SDL_SCANCODE_RSHIFT => UiKey.Shift,
        SDL_Scancode.SDL_SCANCODE_A => UiKey.A,
        SDL_Scancode.SDL_SCANCODE_C => UiKey.C,
        SDL_Scancode.SDL_SCANCODE_D => UiKey.D,
        SDL_Scancode.SDL_SCANCODE_S => UiKey.S,
        SDL_Scancode.SDL_SCANCODE_V => UiKey.V,
        SDL_Scancode.SDL_SCANCODE_W => UiKey.W,
        SDL_Scancode.SDL_SCANCODE_X => UiKey.X,
        SDL_Scancode.SDL_SCANCODE_Y => UiKey.Y,
        SDL_Scancode.SDL_SCANCODE_Z => UiKey.Z,
        _ => null,
    };
}
