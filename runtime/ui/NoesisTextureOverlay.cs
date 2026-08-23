using System;
using Godot;
using Paradise.Ui.Noesis;
using WebGpuSharp;

namespace ParadiseGodot.Runtime.Ui;

/// <summary>The Godot render half of NoesisGUI: the shared <see cref="NoesisViewCore"/> (sim
/// thread) renders through the engine's managed WebGPU <see cref="NoesisRenderDevice"/> on a
/// HEADLESS Dawn device owned by this node — a separate GPU device from Godot's renderer
/// (Dawn instances cannot share objects with Godot's Metal/Vulkan device). Each frame renders
/// the UI into an offscreen RGBA8 texture, reads the pixels back, and uploads them into a
/// Godot <see cref="ImageTexture"/> displayed by this full-rect TextureRect with premultiplied
/// -alpha blending (Noesis composites premultiplied over the transparent clear).
///
/// The synchronous MapSync readback stalls ~1-4 ms per frame at 1080p — accepted for
/// play-mode parity UI; double-buffered readback is the upgrade path if it ever matters.
/// Sizes are logical viewport pixels end-to-end (project stretch mode `canvas_items`).</summary>
public sealed partial class NoesisTextureOverlay : TextureRect
{
    private NoesisViewCore _core = null!;
    private Device? _device;
    private Queue? _queue;
    private NoesisRenderDevice? _renderDevice;
    private global::WebGpuSharp.Texture? _target;
    private TextureView? _targetView;
    private global::WebGpuSharp.Buffer? _readback;
    private uint _w, _h;
    private byte[] _pixels = [];
    private Image? _image;
    private ImageTexture? _texture;
    private bool _rendererInitialized;

    /// <summary>Create the headless Dawn device; false (with a warning) when the WebGPU
    /// native library or an adapter is unavailable — the overlay then stays inert and the
    /// caller should skip creating the Noesis view core entirely.</summary>
    public bool TryInitialize(NoesisViewCore core, Vector2I size)
    {
        _core = core;
        try
        {
            var instance = WebGPU.CreateInstance();
            var options = new RequestAdapterOptions
            {
                CompatibleSurface = null!,
                PowerPreference = PowerPreference.HighPerformance,
                FeatureLevel = FeatureLevel.Core,
            };
            var adapter = instance?.RequestAdapterSync(in options, 10_000_000_000UL);
            var descriptor = new DeviceDescriptor
            {
                Label = "GodotNoesisOverlay",
                UncapturedErrorCallback = static (type, message) =>
                    GD.PushError($"[NoesisTextureOverlay][wgpu {type}] {message.ToString()}"),
            };
            _device = adapter?.RequestDeviceSync(in descriptor, 10_000_000_000UL);
        }
        catch (DllNotFoundException e)
        {
            GD.PushWarning($"[NoesisTextureOverlay] WebGPU native unavailable — Noesis overlay disabled: {e.Message}");
            return false;
        }
        if (_device is null)
        {
            GD.PushWarning("[NoesisTextureOverlay] no WebGPU adapter — Noesis overlay disabled.");
            return false;
        }
        _queue = _device.GetQueue();

        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        StretchMode = StretchModeEnum.Scale;
        Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.PremultAlpha };
        OnResize(size);
        return true;
    }

    /// <summary>Recreate the offscreen target + readback at a new logical size. The sim-side
    /// view resizes separately via the UiEvent.Resize the bridge enqueues.</summary>
    public void OnResize(Vector2I size)
    {
        if (_device is null || size.X <= 0 || size.Y <= 0) return;
        _w = (uint)size.X;
        _h = (uint)size.Y;
        _target = _device.CreateTexture(new TextureDescriptor
        {
            Label = "GodotNoesisOverlay.Target",
            Size = new Extent3D(_w, _h, 1),
            Format = WebGpuSharp.TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = TextureDimension.D2,
        });
        _targetView = _target?.CreateView();
        var padded = PaddedBytesPerRow(_w);
        _readback = _device.CreateBuffer(new BufferDescriptor
        {
            Label = "GodotNoesisOverlay.Readback",
            Size = (ulong)padded * _h,
            Usage = BufferUsage.MapRead | BufferUsage.CopyDst,
            MappedAtCreation = false,
        });
        _pixels = new byte[_w * _h * 4];
        _image = Image.CreateFromData((int)_w, (int)_h, false, Image.Format.Rgba8, _pixels);
        _texture = ImageTexture.CreateFromImage(_image);
        Texture = _texture;
    }

    public override void _Process(double delta)
    {
        if (_device is null || _targetView is null || _readback is null) return;
        var view = _core.View;
        if (view is null) return; // sim thread has not created the UI yet — skip this frame

        if (!_rendererInitialized)
        {
            // Deliberately outside the core's sync lock: Noesis runs Renderer.Init on the
            // render thread while the View lives on the UI (sim) thread — Init touches only
            // render-side state. Only UpdateRenderTree synchronizes the two trees.
            _renderDevice = new NoesisRenderDevice(_device, WebGpuSharp.TextureFormat.RGBA8Unorm);
            view.Renderer.Init(_renderDevice);
            _renderDevice.PrewarmPipelines();
            _rendererInitialized = true;
        }

        if (!_core.TryUpdateRenderTree()) return;

        var encoder = _device.CreateCommandEncoder();

        // NoesisRenderDevice.BeginFrame's onscreen pass uses LoadOp.Load (composite-over
        // semantics), so the overlay target must be cleared to TRANSPARENT first.
        var clearAttachments = new RenderPassColorAttachment[]
        {
            new()
            {
                View = _targetView,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearValue = new WebGpuSharp.Color(0.0, 0.0, 0.0, 0.0),
                DepthSlice = null,
            },
        };
        var clearDescriptor = new RenderPassDescriptor { ColorAttachments = clearAttachments };
        encoder.BeginRenderPass(in clearDescriptor).End();

        _renderDevice!.BeginFrame(encoder, _targetView, _w, _h);
        view.Renderer.RenderOffscreen();
        view.Renderer.Render();
        _renderDevice.EndFrame();

        var padded = PaddedBytesPerRow(_w);
        var src = new TexelCopyTextureInfo { Texture = _target!, MipLevel = 0 };
        var dst = new TexelCopyBufferInfo
        {
            Buffer = _readback,
            Layout = new TexelCopyBufferLayout { Offset = 0, BytesPerRow = padded, RowsPerImage = _h },
        };
        var extent = new Extent3D(_w, _h, 1);
        encoder.CopyTextureToBuffer(in src, in dst, in extent);
        _queue!.Submit(encoder.Finish()!);
        _queue.OnSubmittedWorkSync(5_000_000_000UL);

        var mappedSize = (nuint)((ulong)padded * _h);
        _readback.MapSync(MapMode.Read, 0, mappedSize, 5_000);
        var (pixels, w, h) = (_pixels, _w, _h);
        _readback.GetConstMappedRange(0, mappedSize, (ReadOnlySpan<byte> mapped) =>
        {
            for (var y = 0; y < h; y++)
            {
                mapped.Slice((int)(y * padded), (int)(w * 4)).CopyTo(pixels.AsSpan((int)(y * w * 4)));
            }
        });
        _readback.Unmap();

        _image!.SetData((int)_w, (int)_h, false, Image.Format.Rgba8, _pixels);
        _texture!.Update(_image);
    }

    private static uint PaddedBytesPerRow(uint width) => (width * 4 + 255u) & ~255u;
}
