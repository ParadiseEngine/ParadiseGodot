using System.Numerics;
using Paradise.Assets.Gltf;
using Paradise.ECS;
using Paradise.Rendering;
using Paradise.Rendering.Pbr;
using Paradise.Rendering.WebGPU;
using Paradise.Sample.ImGui;
using Paradise.Sample.Odyssey;
using Paradise.Ui;
using Paradise.Sample.Ui;
using static SDL.SDL3;
using SDL;
using Paradise.Windowing;

namespace Paradise.Sample.Runtime;

/// <summary>Standalone host for the "Space Odyssey" sample (<c>--game odyssey</c>): a piloted 3D
/// spaceship flying a procedural sector map (a star, orbiting planets, asteroids, a glowing warp gate).
/// The sim ticks on the <see cref="OdysseyRunner"/>'s own 60 Hz thread; this host samples its snapshot
/// pair at (now − 2/60) and Lerp/Slerps every body's transform onto a hand-built <see cref="PbrScene"/>
/// of procedural meshes (<see cref="ProcMesh"/>), with a chase camera behind the ship. Pilot with
/// WASD (thrust/turn), hold SPACE to charge the warp drive, then fly into the gate to jump. The ImGui
/// "Star Voyager" HUD (<see cref="OdysseyView"/>) draws as a pure reader overlay. Same sim core as the
/// Godot bridge (<c>scenes/odyssey.tscn</c>).</summary>
internal static class OdysseyHost
{
    private const int Width = 1280;
    private const int Height = 720;
    private const double RenderDelaySeconds = 2.0 / 60.0;
    private const double MaxRenderSampleLagSeconds = 4.0 / 60.0;

    // Chase-camera rig (behind + above the ship, looking slightly ahead).
    private const float ChaseDistance = 14f;
    private const float ChaseHeight = 6f;
    private const float LookAhead = 5f;
    private static readonly float FovYRadians = 60f * MathF.PI / 180f;

    private sealed record RenderNode(Entity Entity, PbrInstance Instance, float Scale);

    private sealed class Composed
    {
        public required ImGuiUi ImGui;
        public required PbrRenderer Pbr;
        public required PbrScene Scene;
        public required List<RenderNode> Nodes;
        public required OdysseyViewModel ViewModel;
        // Last sampled ship transform — the chase camera is rebuilt from this EVERY frame (even one that
        // samples no fresh snapshot), so scene.Camera is never left a degenerate/zero matrix.
        public Vector3 LastShipPos = new(0f, 0f, 20f);
        public Quaternion LastShipRot = Quaternion.Identity;
    }

    public static int Run(int? headlessFrames, string? screenshotPath)
    {
        using var runner = new OdysseyRunner();
        return headlessFrames is { } frames
            ? RunHeadless(runner, frames, screenshotPath)
            : RunWindowed(runner);
    }

    private static int RunHeadless(OdysseyRunner runner, int frameCount, string? screenshotPath)
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
            var pump = new ImGuiSampleRunner();
            var c = Compose(runner, renderer, pump, Width, Height);
            using var _pbr = c.Pbr;

            runner.Start();
            pump.Start();
            runner.SetCharging(true); // headless smoke: fill the drive so a warp+regeneration fires

            double sampleTime = 0;
            for (var i = 0; i < frameCount; i++)
            {
                if (i > 0 && i % 90 == 0)
                {
                    runner.RequestWarp(); // exercise the warp → sector-regeneration path
                }
                Thread.Sleep(8); // let the sim thread produce snapshots to sample
                Frame(runner, c, ref sampleTime, 1.0 / 60.0, Width / (float)Height);
            }

            if (runner.ThreadException is { } ex)
            {
                Console.Error.WriteLine($"[Odyssey] sim thread faulted: {ex}");
                return 1;
            }
            Console.WriteLine(
                $"[Odyssey] Headless: rendered {frameCount} frames, {c.Nodes.Count} instances, sector {runner.Sector}.");

            if (screenshotPath is not null)
            {
                var pixels = renderer.ReadbackColor(out var w, out var h);
                Program.WriteBmp(screenshotPath, pixels, w, h);
                Console.WriteLine($"[Odyssey] Screenshot: {screenshotPath} ({w}x{h}), " +
                    $"{NonClearPixels(pixels)} lit pixels.");
            }
            pump.Dispose();
            return 0;
        }
        finally
        {
            SDL_Quit();
        }
    }

    private static unsafe int RunWindowed(OdysseyRunner runner)
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            Console.Error.WriteLine($"SDL_Init failed: {SDL_GetError()}");
            return 1;
        }

        SDL_Window* window = null;
        IntPtr metalView = IntPtr.Zero;
        WebGpuRenderer? renderer = null;
        ImGuiSampleRunner? pump = null;
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
            pump = new ImGuiSampleRunner();
            var c = Compose(runner, renderer, pump, surfaceDesc.Width, surfaceDesc.Height);
            using var _pbr = c.Pbr;

            int logicalW, logicalH;
            SDL_GetWindowSize(window, &logicalW, &logicalH);
            var uiScale = logicalW > 0 ? surfaceDesc.Width / (float)logicalW : 1f;
            uint width = surfaceDesc.Width, height = surfaceDesc.Height;
            SDL_StartTextInput(window);

            runner.Start();
            pump.Start();
            Console.WriteLine("[Odyssey] WASD = thrust/turn · hold SPACE = charge the warp drive · fly into the gate to jump · N = new voyage.");

            // Pilot latches, updated from key events, pushed to the runner each frame.
            bool fwd = false, back = false, left = false, right = false, charge = false;
            double sampleTime = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var last = clock.Elapsed.TotalSeconds;

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
                                width = (uint)pw; height = (uint)ph;
                                renderer.Resize(width, height);
                                c.Pbr.Resize(width, height);
                                uiScale = lw > 0 ? pw / (float)lw : 1f;
                                pump.EnqueueUiEvent(WindowEvent.Resize(pw, ph));
                            }
                            break;
                        }
                        case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                            pump.EnqueueUiEvent(WindowEvent.PointerMove(ev.motion.x * uiScale, ev.motion.y * uiScale));
                            break;
                        case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN when ToButton(ev.button.button) is { } db:
                            pump.EnqueueUiEvent(WindowEvent.Mouse(db, pressed: true, ev.button.x * uiScale, ev.button.y * uiScale));
                            break;
                        case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP when ToButton(ev.button.button) is { } ub:
                            pump.EnqueueUiEvent(WindowEvent.Mouse(ub, pressed: false, ev.button.x * uiScale, ev.button.y * uiScale));
                            break;
                        case SDL_EventType.SDL_EVENT_KEY_DOWN:
                        case SDL_EventType.SDL_EVENT_KEY_UP:
                        {
                            bool down = type == SDL_EventType.SDL_EVENT_KEY_DOWN;
                            switch (ev.key.scancode)
                            {
                                case SDL_Scancode.SDL_SCANCODE_W: case SDL_Scancode.SDL_SCANCODE_UP: fwd = down; break;
                                case SDL_Scancode.SDL_SCANCODE_S: case SDL_Scancode.SDL_SCANCODE_DOWN: back = down; break;
                                case SDL_Scancode.SDL_SCANCODE_A: case SDL_Scancode.SDL_SCANCODE_LEFT: left = down; break;
                                case SDL_Scancode.SDL_SCANCODE_D: case SDL_Scancode.SDL_SCANCODE_RIGHT: right = down; break;
                                case SDL_Scancode.SDL_SCANCODE_SPACE: charge = down; break;
                                case SDL_Scancode.SDL_SCANCODE_N when down && !ev.key.repeat: runner.RequestNewVoyage(); break;
                                case SDL_Scancode.SDL_SCANCODE_RETURN when down && !ev.key.repeat: runner.RequestWarp(); break;
                            }
                            break;
                        }
                    }
                }

                runner.SetThrust((fwd ? 1f : 0f) - (back ? 1f : 0f));
                runner.SetTurn((right ? 1f : 0f) - (left ? 1f : 0f));
                runner.SetCharging(charge);

                var now = clock.Elapsed.TotalSeconds;
                Frame(runner, c, ref sampleTime, now - last, width / (float)height);
                last = now;
            }

            return 0;
        }
        finally
        {
            pump?.Dispose();
            renderer?.Dispose();
            if (metalView != IntPtr.Zero) SDL_Metal_DestroyView(metalView);
            if (window != null) SDL_DestroyWindow(window);
            SDL_Quit();
        }
    }

    /// <summary>One render frame: sample the snapshot pair, place every instance, drive the chase
    /// camera, and submit. Throws nothing — the caller polls <see cref="OdysseyRunner.ThreadException"/>.</summary>
    private static void Frame(OdysseyRunner runner, Composed c, ref double sampleTime, double frameDelta, float aspect)
    {
        if (runner.HasSnapshots &&
            runner.TrySampleInterpolation(NextSampleTime(runner, ref sampleTime, frameDelta), out var a, out var b, out var alpha))
        {
            alpha = Math.Clamp(alpha, 0f, 1f);
            Entity ship = runner.Ship;
            foreach (var node in c.Nodes)
            {
                Entity e = node.Entity;
                if (!a.IsAlive(e) || !b.IsAlive(e)) continue;
                var pos = Vector3.Lerp(
                    a.GetComponent<Position>(e).Value, b.GetComponent<Position>(e).Value, alpha);
                var rot = Quaternion.Slerp(
                    a.GetComponent<Rotation>(e).Value, b.GetComponent<Rotation>(e).Value, alpha);
                node.Instance.Model =
                    Matrix4x4.CreateScale(node.Scale)
                    * Matrix4x4.CreateFromQuaternion(rot)
                    * Matrix4x4.CreateTranslation(pos);
                if (e == ship) { c.LastShipPos = pos; c.LastShipRot = rot; }
            }
        }

        // Rebuild the chase camera EVERY frame from the last known ship transform (never gated by a
        // successful sample) so scene.Camera is always a valid matrix before RenderFrame.
        var forward = Vector3.Transform(Vector3.UnitZ, c.LastShipRot);
        if (forward.LengthSquared() < 1e-4f) forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward);
        var eye = c.LastShipPos - forward * ChaseDistance + new Vector3(0f, ChaseHeight, 0f);
        c.Scene.Camera = new PbrCamera
        {
            View = PbrMath.LookAt(eye, c.LastShipPos + forward * LookAhead, Vector3.UnitY),
            Projection = PbrMath.Perspective(FovYRadians, aspect, 0.1f, 800f),
            Position = eye,
        };

        c.Scene.ElapsedSeconds += (float)frameDelta;
        c.Pbr.RenderFrame(c.Scene);
    }

    private static double NextSampleTime(OdysseyRunner runner, ref double sampleTime, double frameDelta)
    {
        var target = Math.Min(runner.Now - RenderDelaySeconds, runner.LatestSnapshotTime);
        sampleTime = sampleTime <= 0.0 ? target : Math.Min(sampleTime + frameDelta, target);
        if (target - sampleTime > MaxRenderSampleLagSeconds)
        {
            sampleTime = target;
        }
        return sampleTime;
    }

    private static Composed Compose(OdysseyRunner runner, WebGpuRenderer renderer, ImGuiSampleRunner pump, uint width, uint height)
    {
        // ImGui HUD: a pure reader overlay. The sim owns its own thread now, so the pump only builds
        // the ImGui frame from the (thread-safe) ViewModel — no OnSimTick.
        var imgui = new ImGuiUi(width, height);
        var vm = new OdysseyViewModel(runner);
        var view = new OdysseyView();
        imgui.AddDraw(() => view.Draw(vm));
        imgui.Attach(renderer);
        renderer.OverlayPass = imgui.RecordOverlay;
        pump.UiInput = imgui.Input;

        var pbr = new PbrRenderer(renderer, width, height);
        var scene = new PbrScene
        {
            ClearColor = new ColorRgba(0.01f, 0.01f, 0.03f, 1f), // deep space
            Ambient = new PbrAmbient
            {
                Sky = new Vector3(0.05f, 0.06f, 0.10f),
                Equator = new Vector3(0.03f, 0.03f, 0.06f),
                Ground = new Vector3(0.01f, 0.01f, 0.02f),
                Exposure = 1f,
            },
            Bloom = new PbrBloom { Enabled = true, Threshold = 0.85f, Knee = 0.35f, Intensity = 0.55f },
        };
        // The star is the key light (a warm point at the origin); a dim cold directional fills shadows.
        scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Point,
            Position = Vector3.Zero,
            Color = new Vector3(1f, 0.9f, 0.7f),
            Intensity = 42f,
            Range = 240f,
        });
        scene.Lights.Add(new PbrLight
        {
            Type = PbrLightType.Directional,
            Direction = Vector3.Normalize(new Vector3(-0.3f, -1f, -0.25f)),
            Color = new Vector3(0.35f, 0.4f, 0.55f),
            Intensity = 0.6f,
        });

        // Upload each geometry ONCE; clone the primitive per body with a distinct material id (shared
        // GPU buffers, per-body colour) — the SceneAssembler pattern.
        int greyId = pbr.Materials.AddDefaultMaterial(new Vector4(0.55f, 0.55f, 0.58f, 1f), 0.05f, 0.95f);
        var (sphereV, sphereI) = ProcMesh.Sphere();
        PbrPrimitive spherePrim = pbr.UploadPrimitive(sphereV, sphereI, greyId, dynamic: false);
        var (shipV, shipI) = ProcMesh.Ship();
        int shipMatId = pbr.Materials.AddDefaultMaterial(new Vector4(0.62f, 0.66f, 0.78f, 1f), 0.65f, 0.35f);
        PbrPrimitive shipPrim = pbr.UploadPrimitive(shipV, shipI, shipMatId, dynamic: false);
        var (torusV, torusI) = ProcMesh.Torus();
        PbrPrimitive torusPrim = pbr.UploadPrimitive(torusV, torusI, greyId, dynamic: false);

        var nodes = new List<RenderNode>();

        // Ship instance.
        var shipInstance = new PbrInstance { Mesh = new PbrMesh(new[] { shipPrim }), Model = Matrix4x4.Identity };
        scene.Instances.Add(shipInstance);
        nodes.Add(new RenderNode(runner.Ship, shipInstance, 1f));

        // Body instances.
        foreach (var body in runner.Bodies)
        {
            var tint = body.Tint;
            int matId = body.Kind switch
            {
                0 => Emissive(pbr, new Vector3(tint.X, tint.Y, tint.Z), 2.4f),  // star
                3 => Emissive(pbr, new Vector3(tint.X, tint.Y, tint.Z), 2.0f),  // warp gate
                1 => pbr.Materials.AddDefaultMaterial(tint, 0.10f, 0.80f),      // planet
                _ => greyId,                                                     // asteroid
            };
            PbrPrimitive basePrim = body.Kind == 3 ? torusPrim : spherePrim;
            var inst = new PbrInstance
            {
                Mesh = new PbrMesh(new[] { basePrim with { MaterialId = matId } }),
                Model = Matrix4x4.Identity,
            };
            scene.Instances.Add(inst);
            nodes.Add(new RenderNode(body.Entity, inst, body.Scale));
        }

        return new Composed { ImGui = imgui, Pbr = pbr, Scene = scene, Nodes = nodes, ViewModel = vm };
    }

    /// <summary>A self-lit (emissive) material for the star and gate — a bright emissive factor so the
    /// body glows and blooms; double-sided so the thin gate ring reads from either face.</summary>
    private static int Emissive(PbrRenderer pbr, Vector3 color, float strength)
    {
        // Black base so the body is PURE emission (a light source is uniformly bright, not shaded like a
        // lit sphere — a non-zero base would catch ambient/fill light and show a bright/dark side).
        var mat = new GltfMaterialData(
            Name: "emissive",
            BaseColorFactor: new Vector4(0f, 0f, 0f, 1f),
            MetallicFactor: 0f,
            RoughnessFactor: 1f,
            EmissiveFactor: color * strength,
            NormalScale: 1f,
            OcclusionStrength: 1f,
            TransmissionFactor: 0f,
            AlphaMode: GltfAlphaMode.Opaque,
            AlphaCutoff: 0.5f,
            DoubleSided: true,
            BaseColorImage: -1,
            MetallicRoughnessImage: -1,
            NormalImage: -1,
            OcclusionImage: -1,
            EmissiveImage: -1,
            BaseColorUvTransform: default);
        return pbr.Materials.AddMaterial(in mat, Array.Empty<GltfImageData>());
    }

    private static int NonClearPixels(byte[] bgra)
    {
        // Count pixels brighter than the near-black clear — a non-empty-frame sanity check.
        int count = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i] > 12 || bgra[i + 1] > 12 || bgra[i + 2] > 12) count++;
        }
        return count;
    }

    private static PointerButton? ToButton(byte sdlButton) => sdlButton switch
    {
        (byte)SDL_BUTTON_LEFT => PointerButton.Left,
        (byte)SDL_BUTTON_RIGHT => PointerButton.Right,
        (byte)SDL_BUTTON_MIDDLE => PointerButton.Middle,
        _ => null,
    };
}
