using System.Diagnostics;
using System.Numerics;
using Paradise.Rendering;
using Paradise.Rendering.WebGPU;
using static SDL.SDL3;
using SDL;

namespace ParadiseRuntime;

/// <summary>The standalone Paradise runtime: loads an exported scene from <c>data/</c>, runs
/// the real 60 Hz game simulation (ParadiseGame's SimulationRunner + MovementSystem), and
/// PBR-renders interpolated snapshots in an SDL window. WASD moves the player
/// camera-relative; left-click paths via the navmesh. <c>--headless N</c> renders N frames
/// offscreen for CI.
///
/// Usage: ParadiseRuntime --scene data/scenes/sample.json [--headless N] [--ortho] [--fov N]</summary>
internal static class Program
{
    private const int InitialWidth = 1280;
    private const int InitialHeight = 720;

    private static int Main(string[] args)
    {
        string scenePath = "data/scenes/sample.json";
        int? headlessFrames = null;
        var orthographic = false;
        var fovDegrees = 75f; // Godot Camera3D default (vertical)
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scene" when i + 1 < args.Length:
                    scenePath = args[++i];
                    break;
                case "--headless" when i + 1 < args.Length && int.TryParse(args[i + 1], out var frames):
                    headlessFrames = frames;
                    i++;
                    break;
                case "--ortho":
                    orthographic = true;
                    break;
                case "--fov" when i + 1 < args.Length && float.TryParse(args[i + 1], out var fov):
                    fovDegrees = fov;
                    i++;
                    break;
            }
        }

        try
        {
            var level = LevelLoader.Load(scenePath);
            Console.WriteLine(
                $"[ParadiseRuntime] {scenePath}: {level.Level.Entities.Count} entities, " +
                $"{level.MeshAssets.Count} mesh assets, {level.Materials.Count} materials.");
            return headlessFrames is { } n
                ? RunHeadless(level, n, orthographic, fovDegrees)
                : RunWindowed(level, orthographic, fovDegrees);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ParadiseRuntime failed: {ex}");
            return 1;
        }
    }

    private static int RunHeadless(RuntimeLevel level, int frameCount, bool orthographic, float fovDegrees)
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
            using var loop = new RuntimeLoop(level, renderer, InitialWidth, InitialHeight, orthographic, fovDegrees);
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
                $"[ParadiseRuntime] Headless: rendered {frameCount} frames, {loop.InstanceCount} instances, " +
                $"player={(loop.HasPlayer ? "yes" : "no")}, collision={(loop.CollisionWorld is not null ? "yes" : "no")}.");
            return 0;
        }
        finally
        {
            SDL_Quit();
        }
    }

    private static unsafe int RunWindowed(RuntimeLevel level, bool orthographic, float fovDegrees)
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
            using var loop = new RuntimeLoop(level, renderer, surfaceDesc.Width, surfaceDesc.Height, orthographic, fovDegrees);
            loop.Start();
            Console.WriteLine("[ParadiseRuntime] WASD moves the player (camera-relative); left-click to path-move.");

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
                        }
                    }
                    else if (type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN &&
                             ev.button.button == SDL_BUTTON_LEFT)
                    {
                        loop.TryClickMove(new Vector2(ev.button.x, ev.button.y));
                    }
                }

                loop.SetMoveInput(ReadWasdDirection(loop));

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

    /// <summary>Camera-relative planar WASD (the EcsSceneBridge.ReadWasdDirection port).</summary>
    private static unsafe Vector3 ReadWasdDirection(RuntimeLoop loop)
    {
        int keyCount;
        var keys = SDL_GetKeyboardState(&keyCount);
        var (forward, right) = loop.PlanarBasis();
        var direction = Vector3.Zero;
        if (IsDown(keys, keyCount, SDL_Scancode.SDL_SCANCODE_W)) direction += forward;
        if (IsDown(keys, keyCount, SDL_Scancode.SDL_SCANCODE_S)) direction -= forward;
        if (IsDown(keys, keyCount, SDL_Scancode.SDL_SCANCODE_A)) direction -= right;
        if (IsDown(keys, keyCount, SDL_Scancode.SDL_SCANCODE_D)) direction += right;
        var length = direction.Length();
        return length > 1e-4f ? direction / length : Vector3.Zero;
    }

    private static unsafe bool IsDown(SDLBool* keys, int keyCount, SDL_Scancode scancode)
    {
        var index = (int)scancode;
        return index < keyCount && keys[index];
    }
}
