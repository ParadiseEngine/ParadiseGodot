using System.Runtime.InteropServices;
using Noesis;
using WebGpuSharp;

namespace NoesisSpike;

/// <summary>Stage-2: a real PURE MANAGED Noesis RenderDevice on WebGPU (wgpu-native via
/// WebGPUSharp — the exact stack ParadiseEngine renders with). Implements the two shader
/// variants the spike XAML exercises (Path_AA_Solid, Path_AA_Linear) with the full generic
/// machinery a production device needs: dynamic vertex/index buffers (pinned map → WriteBuffer
/// on unmap), a 256-aligned uniform ring with dynamic offsets, texture creation/upload
/// (Ramps RGBA8 + Glyphs R8), premultiplied SrcOver blending, and per-shader pipelines keyed
/// by Batch.Shader.Index. Unimplemented shaders are counted in SkippedShaders instead of
/// crashing, so the spike degrades visibly rather than fatally.</summary>
internal sealed class WebGpuRenderDevice : Noesis.RenderDevice, IDisposable
{
    public readonly HashSet<string> SkippedShaders = new();

    private const uint VertexBufferSize = 4 * 1024 * 1024;
    private const uint IndexBufferSize = 1024 * 1024;
    private const uint UniformSlotSize = 256;
    private const uint UniformSlots = 256;

    private readonly int _width;
    private readonly int _height;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly WebGpuSharp.Texture _target;
    private readonly TextureView _targetView;
    private readonly WebGpuSharp.Buffer _vertexBuffer;
    private readonly WebGpuSharp.Buffer _indexBuffer;
    private readonly WebGpuSharp.Buffer _uniformBuffer;
    private readonly Sampler _sampler;
    private readonly BindGroupLayout _bindGroupLayout;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Dictionary<int, RenderPipeline> _pipelines = new();

    private readonly byte[] _vertexScratch = new byte[VertexBufferSize];
    private readonly byte[] _indexScratch = new byte[IndexBufferSize];
    private GCHandle _vertexPin;
    private GCHandle _indexPin;
    private uint _mappedVertexBytes;
    private uint _mappedIndexBytes;

    private CommandEncoder? _encoder;
    private RenderPassEncoder? _pass;
    private BindGroup? _bindGroup;
    private uint _uniformCursor;
    private readonly byte[] _uniformScratch = new byte[UniformSlotSize];

    private sealed class SpikeTexture : Noesis.Texture
    {
        public SpikeTexture(WebGpuSharp.Texture native, TextureView view, uint width, uint height, Noesis.TextureFormat format)
        {
            Native = native; View = view; W = width; H = height; Format = format;
        }

        public WebGpuSharp.Texture Native { get; }
        public TextureView View { get; }
        public uint W { get; }
        public uint H { get; }
        public Noesis.TextureFormat Format { get; }
        public override uint Width => W;
        public override uint Height => H;
        public override bool HasMipMaps => false;
        public override bool IsInverted => false;
        public override bool HasAlpha => Format != Noesis.TextureFormat.RGBX8;
    }

    public WebGpuRenderDevice(int width, int height)
    {
        _width = width;
        _height = height;

        var instance = WebGPU.CreateInstance()
            ?? throw new InvalidOperationException("WebGPU.CreateInstance returned null.");
        var adapterOptions = new RequestAdapterOptions
        {
            CompatibleSurface = null!,
            PowerPreference = PowerPreference.HighPerformance,
            FeatureLevel = FeatureLevel.Core,
        };
        var adapter = instance.RequestAdapterSync(in adapterOptions, 10_000_000_000UL)
            ?? throw new InvalidOperationException("No WebGPU adapter.");
        var deviceDesc = new DeviceDescriptor
        {
            Label = "NoesisSpike",
            UncapturedErrorCallback = static (type, message) =>
                Console.Error.WriteLine($"[NoesisSpike][wgpu {type}] {message.ToString()}"),
        };
        _device = adapter.RequestDeviceSync(in deviceDesc, 10_000_000_000UL)
            ?? throw new InvalidOperationException("WebGPU device request failed.");
        _queue = _device.GetQueue()!;

        _target = _device.CreateTexture(new TextureDescriptor
        {
            Label = "NoesisTarget",
            Size = new Extent3D((uint)width, (uint)height, 1),
            Format = WebGpuSharp.TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            MipLevelCount = 1,
            SampleCount = 1,
            Dimension = TextureDimension.D2,
        })!;
        _targetView = _target.CreateView()!;

        _vertexBuffer = CreateBuffer("NoesisVB", VertexBufferSize, BufferUsage.Vertex | BufferUsage.CopyDst);
        _indexBuffer = CreateBuffer("NoesisIB", IndexBufferSize, BufferUsage.Index | BufferUsage.CopyDst);
        _uniformBuffer = CreateBuffer("NoesisUniforms", UniformSlotSize * UniformSlots, BufferUsage.Uniform | BufferUsage.CopyDst);
        _vertexPin = GCHandle.Alloc(_vertexScratch, GCHandleType.Pinned);
        _indexPin = GCHandle.Alloc(_indexScratch, GCHandleType.Pinned);

        _sampler = _device.CreateSampler(new SamplerDescriptor
        {
            Label = "NoesisLinearClamp",
            MinFilter = FilterMode.Linear,
            MagFilter = FilterMode.Linear,
            MipmapFilter = MipmapFilterMode.Nearest,
            AddressModeU = AddressMode.ClampToEdge,
            AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge,
        })!;

        // One layout for every spike pipeline: vs uniforms (dynamic), fs uniforms (dynamic),
        // ramps texture + sampler. Solid batches simply don't read bindings 1-3.
        _bindGroupLayout = _device.CreateBindGroupLayout(new BindGroupLayoutDescriptor
        {
            Entries =
            [
                new BindGroupLayoutEntry
                {
                    Binding = 0,
                    Visibility = ShaderStage.Vertex,
                    Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, HasDynamicOffset = true, MinBindingSize = 64 },
                },
                new BindGroupLayoutEntry
                {
                    Binding = 1,
                    Visibility = ShaderStage.Fragment,
                    Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, HasDynamicOffset = true, MinBindingSize = 16 },
                },
                new BindGroupLayoutEntry
                {
                    Binding = 2,
                    Visibility = ShaderStage.Fragment,
                    Texture = new TextureBindingLayout { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.D2, Multisampled = false },
                },
                new BindGroupLayoutEntry
                {
                    Binding = 3,
                    Visibility = ShaderStage.Fragment,
                    Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering },
                },
            ],
        })!;
        _pipelineLayout = _device.CreatePipelineLayout(new PipelineLayoutDescriptor
        {
            BindGroupLayouts = [_bindGroupLayout],
        })!;
    }

    private WebGpuSharp.Buffer CreateBuffer(string label, ulong size, BufferUsage usage) =>
        _device.CreateBuffer(new BufferDescriptor { Label = label, Size = size, Usage = usage, MappedAtCreation = false })
            ?? throw new InvalidOperationException($"{label} creation failed.");

    // ---- Noesis RenderDevice ----

    public override DeviceCaps Caps => new()
    {
        CenterPixelOffset = 0f,
        LinearRendering = false,
        SubpixelRendering = false,
        DepthRangeZeroToOne = true,
        ClipSpaceYInverted = false,
    };

    public override Noesis.Texture CreateTexture(string label, uint width, uint height, uint numLevels, Noesis.TextureFormat format, IntPtr data)
    {
        var wgFormat = format == Noesis.TextureFormat.R8 ? WebGpuSharp.TextureFormat.R8Unorm : WebGpuSharp.TextureFormat.RGBA8Unorm;
        var native = _device.CreateTexture(new TextureDescriptor
        {
            Label = label,
            Size = new Extent3D(width, height, 1),
            Format = wgFormat,
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            MipLevelCount = numLevels,
            SampleCount = 1,
            Dimension = TextureDimension.D2,
        })!;
        var texture = new SpikeTexture(native, native.CreateView()!, width, height, format);
        if (data != IntPtr.Zero)
        {
            UpdateTexture(texture, 0, 0, 0, width, height, data);
        }
        if (label == "Ramps")
        {
            // The shared bind group binds the ramps view; (re)build it now that it exists.
            _bindGroup = _device.CreateBindGroup(new BindGroupDescriptor
            {
                Label = "NoesisBindGroup",
                Layout = _bindGroupLayout,
                Entries =
                [
                    new BindGroupEntry { Binding = 0, Buffer = _uniformBuffer, Offset = 0, Size = 64 },
                    new BindGroupEntry { Binding = 1, Buffer = _uniformBuffer, Offset = 0, Size = 16 },
                    new BindGroupEntry { Binding = 2, TextureView = texture.View },
                    new BindGroupEntry { Binding = 3, Sampler = _sampler },
                ],
            })!;
        }
        return texture;
    }

    public override unsafe void UpdateTexture(Noesis.Texture texture, uint level, uint x, uint y, uint width, uint height, IntPtr data)
    {
        var spike = (SpikeTexture)texture;
        var bytesPerPixel = spike.Format == Noesis.TextureFormat.R8 ? 1u : 4u;
        var destination = new TexelCopyTextureInfo
        {
            Texture = spike.Native,
            MipLevel = level,
            Origin = new Origin3D(x, y, 0),
        };
        var layout = new TexelCopyBufferLayout { Offset = 0, BytesPerRow = width * bytesPerPixel, RowsPerImage = height };
        var size = new Extent3D(width, height, 1);
        var span = new ReadOnlySpan<byte>((void*)data, (int)(width * height * bytesPerPixel));
        _queue.WriteTexture(destination, span, layout, size);
    }

    public override RenderTarget CreateRenderTarget(string label, uint width, uint height, uint sampleCount, bool needsPingPong)
        => throw new NotSupportedException("Offscreen render targets (opacity groups/effects) are out of spike scope.");

    public override RenderTarget CloneRenderTarget(string label, RenderTarget surface)
        => throw new NotSupportedException("Offscreen render targets are out of spike scope.");

    public override void SetRenderTarget(RenderTarget surface) { }
    public override void BeginTile(RenderTarget surface, Tile tile) { }
    public override void EndTile(RenderTarget surface) { }
    public override void ResolveRenderTarget(RenderTarget surface, Tile[] tiles) { }
    public override void BeginOffscreenRender() { }
    public override void EndOffscreenRender() { }
    public override void BeginOnscreenRender() { }
    public override void EndOnscreenRender() { }

    public override IntPtr MapVertices(uint bytes)
    {
        _mappedVertexBytes = bytes;
        return _vertexPin.AddrOfPinnedObject();
    }

    public override void UnmapVertices()
    {
        if (_mappedVertexBytes > 0)
        {
            _queue.WriteBuffer(_vertexBuffer, 0, _vertexScratch.AsSpan(0, (int)_mappedVertexBytes));
        }
        _mappedVertexBytes = 0;
    }

    public override IntPtr MapIndices(uint bytes)
    {
        _mappedIndexBytes = bytes;
        return _indexPin.AddrOfPinnedObject();
    }

    public override void UnmapIndices()
    {
        if (_mappedIndexBytes > 0)
        {
            _queue.WriteBuffer(_indexBuffer, 0, _indexScratch.AsSpan(0, (int)_mappedIndexBytes));
        }
        _mappedIndexBytes = 0;
    }

    public override unsafe void DrawBatch(ref Batch batch)
    {
        if (_pass is null || _bindGroup is null)
        {
            SkippedShaders.Add($"{batch.Shader.Name}(no pass)");
            return;
        }
        if (!TryGetPipeline(batch.Shader.Index, batch.Shader.Name, out var pipeline))
        {
            SkippedShaders.Add(batch.Shader.Name);
            return;
        }

        // Uniform ring: one 256B slot per stage per batch, bound via dynamic offsets.
        var vsOffset = WriteUniform(batch.VertexUniform0);
        var psOffset = WriteUniform(batch.PixelUniform0);

        var pass = _pass.Value;
        pass.SetPipeline(pipeline);
        ReadOnlySpan<uint> offsets = [vsOffset, psOffset];
        pass.SetBindGroup(0, _bindGroup, offsets);
        pass.SetVertexBuffer(0, _vertexBuffer, batch.VertexOffset, VertexBufferSize - batch.VertexOffset);
        pass.SetIndexBuffer(_indexBuffer, WebGpuSharp.IndexFormat.Uint16, 0, IndexBufferSize);
        pass.DrawIndexed(batch.NumIndices, 1, batch.StartIndex, 0, 0);
    }

    private unsafe uint WriteUniform(in UniformData data)
    {
        var slotOffset = _uniformCursor * UniformSlotSize;
        _uniformCursor = (_uniformCursor + 1) % UniformSlots;
        Array.Clear(_uniformScratch);
        if (data.NumWords > 0 && data.Values != IntPtr.Zero)
        {
            new ReadOnlySpan<byte>((void*)data.Values, (int)data.NumWords * 4)
                .CopyTo(_uniformScratch);
        }
        _queue.WriteBuffer(_uniformBuffer, slotOffset, _uniformScratch);
        return slotOffset;
    }

    // ---- frame driving (called by the spike harness around view.Renderer.Render()) ----

    public void BeginFrame()
    {
        _encoder = _device.CreateCommandEncoder();
        var colors = new RenderPassColorAttachment[]
        {
            new()
            {
                View = _targetView,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearValue = new WebGpuSharp.Color(0, 0, 0, 0),
                DepthSlice = null,
            },
        };
        var passDesc = new RenderPassDescriptor { ColorAttachments = colors };
        _pass = _encoder!.Value.BeginRenderPass(in passDesc);
        _uniformCursor = 0;
    }

    public byte[] EndFrameAndRead()
    {
        _pass!.Value.End();
        _pass = null;
        var encoder = _encoder!.Value;

        const uint bytesPerPixel = 4;
        var unpadded = (uint)_width * bytesPerPixel;
        var padded = (unpadded + 255u) & ~255u;
        var readback = CreateBuffer("NoesisReadback", (ulong)padded * (uint)_height, BufferUsage.MapRead | BufferUsage.CopyDst);
        var source = new TexelCopyTextureInfo { Texture = _target, MipLevel = 0 };
        var destination = new TexelCopyBufferInfo
        {
            Buffer = readback,
            Layout = new TexelCopyBufferLayout { Offset = 0, BytesPerRow = padded, RowsPerImage = (uint)_height },
        };
        var copySize = new Extent3D((uint)_width, (uint)_height, 1);
        encoder.CopyTextureToBuffer(in source, in destination, in copySize);
        _queue.Submit(encoder.Finish()!);
        _encoder = null;

        _queue.OnSubmittedWorkSync(5_000_000_000UL);
        var size = (nuint)((ulong)padded * (uint)_height);
        readback.MapSync(MapMode.Read, 0, size, 5_000);
        var pixels = new byte[unpadded * _height];
        readback.GetConstMappedRange(0, size, (ReadOnlySpan<byte> mapped) =>
        {
            for (var y = 0; y < _height; y++)
            {
                mapped.Slice((int)(y * padded), (int)unpadded).CopyTo(pixels.AsSpan((int)(y * unpadded)));
            }
        });
        readback.Unmap();
        readback.Destroy();
        return pixels;
    }

    // ---- pipelines ----

    private bool TryGetPipeline(int shaderIndex, string shaderName, out RenderPipeline pipeline)
    {
        if (_pipelines.TryGetValue(shaderIndex, out pipeline!))
        {
            return true;
        }
        var built = shaderName switch
        {
            "Path_AA_Solid" => BuildPipeline(shaderName, SolidWgsl, PosColorCoverageLayout()),
            "Path_AA_Linear" => BuildPipeline(shaderName, LinearWgsl, PosTex0CoverageLayout()),
            _ => null,
        };
        if (built is null)
        {
            pipeline = null!;
            return false;
        }
        _pipelines[shaderIndex] = built;
        pipeline = built;
        return true;
    }

    private static VertexBufferLayout PosColorCoverageLayout() => new()
    {
        ArrayStride = 16, // Pos f32x2 + Color u8x4 + Coverage f32
        StepMode = VertexStepMode.Vertex,
        Attributes = new VertexAttribute[]
        {
            new() { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
            new() { Format = VertexFormat.Unorm8x4, Offset = 8, ShaderLocation = 1 },
            new() { Format = VertexFormat.Float32, Offset = 12, ShaderLocation = 2 },
        },
    };

    private static VertexBufferLayout PosTex0CoverageLayout() => new()
    {
        ArrayStride = 20, // Pos f32x2 + Tex0 f32x2 + Coverage f32
        StepMode = VertexStepMode.Vertex,
        Attributes = new VertexAttribute[]
        {
            new() { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
            new() { Format = VertexFormat.Float32x2, Offset = 8, ShaderLocation = 1 },
            new() { Format = VertexFormat.Float32, Offset = 16, ShaderLocation = 2 },
        },
    };

    private RenderPipeline BuildPipeline(string name, string wgsl, VertexBufferLayout vertexLayout)
    {
        var moduleDesc = new ShaderModuleWGSLDescriptor { Code = wgsl };
        var module = _device.CreateShaderModuleWGSL(name, in moduleDesc)
            ?? throw new InvalidOperationException($"{name}: WGSL compile failed.");

        var colorTargets = new ColorTargetState[]
        {
            new()
            {
                Format = WebGpuSharp.TextureFormat.RGBA8Unorm,
                // Noesis SrcOver with premultiplied output: One / OneMinusSrcAlpha.
                Blend = new BlendState
                {
                    Color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha },
                    Alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha },
                },
                WriteMask = ColorWriteMask.All,
            },
        };
        var pipelineDesc = new RenderPipelineDescriptor
        {
            Label = name,
            Layout = _pipelineLayout,
            Vertex = new VertexState
            {
                Module = module,
                EntryPoint = "vs_main",
                Buffers = new WebGpuManagedSpan<VertexBufferLayout>(new[] { vertexLayout }),
            },
            Fragment = new FragmentState
            {
                Module = module,
                EntryPoint = "fs_main",
                Targets = new WebGpuManagedSpan<ColorTargetState>(colorTargets),
            },
            Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList },
            Multisample = new MultisampleState { Count = 1, Mask = uint.MaxValue },
        };
        return _device.CreateRenderPipelineSync(in pipelineDesc)
            ?? throw new InvalidOperationException($"{name}: pipeline creation failed.");
    }

    // Noesis conventions: projection is 16 dwords in VertexUniform0, row-vector multiply
    // (mul(pos, proj) in the reference HLSL); vertex colors arrive non-premultiplied sRGB and
    // the SrcOver blend expects premultiplied output, so fs premultiplies. Coverage is the
    // PPAA fringe alpha. Path_AA_Linear samples the Ramps LUT at Tex0 and multiplies the
    // 1-dword pixel uniform (opacity).
    private const string SolidWgsl = """
        struct VsUniforms { proj: mat4x4<f32> }
        @group(0) @binding(0) var<uniform> vsU: VsUniforms;

        struct VsOut {
            @builtin(position) pos: vec4<f32>,
            @location(0) color: vec4<f32>,
            @location(1) coverage: f32,
        }

        @vertex fn vs_main(
            @location(0) pos: vec2<f32>,
            @location(1) color: vec4<f32>,
            @location(2) coverage: f32) -> VsOut {
            var o: VsOut;
            o.pos = vec4<f32>(pos, 0.0, 1.0) * vsU.proj;
            o.color = color;
            o.coverage = coverage;
            return o;
        }

        @fragment fn fs_main(i: VsOut) -> @location(0) vec4<f32> {
            let a = i.color.a * i.coverage;
            return vec4<f32>(i.color.rgb * a, a);
        }
        """;

    private const string LinearWgsl = """
        struct VsUniforms { proj: mat4x4<f32> }
        @group(0) @binding(0) var<uniform> vsU: VsUniforms;
        struct PsUniforms { opacity: vec4<f32> }
        @group(0) @binding(1) var<uniform> psU: PsUniforms;
        @group(0) @binding(2) var ramps: texture_2d<f32>;
        @group(0) @binding(3) var samp: sampler;

        struct VsOut {
            @builtin(position) pos: vec4<f32>,
            @location(0) uv: vec2<f32>,
            @location(1) coverage: f32,
        }

        @vertex fn vs_main(
            @location(0) pos: vec2<f32>,
            @location(1) uv: vec2<f32>,
            @location(2) coverage: f32) -> VsOut {
            var o: VsOut;
            o.pos = vec4<f32>(pos, 0.0, 1.0) * vsU.proj;
            o.uv = uv;
            o.coverage = coverage;
            return o;
        }

        @fragment fn fs_main(i: VsOut) -> @location(0) vec4<f32> {
            let paint = textureSample(ramps, samp, i.uv);
            let a = paint.a * psU.opacity.x * i.coverage;
            return vec4<f32>(paint.rgb * a, a);
        }
        """;

    public void Dispose()
    {
        if (_vertexPin.IsAllocated) _vertexPin.Free();
        if (_indexPin.IsAllocated) _indexPin.Free();
    }
}
