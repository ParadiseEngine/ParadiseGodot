using System.Runtime.InteropServices;
using Noesis;

namespace NoesisSpike;

/// <summary>Stage-1 feasibility probe for a PURE MANAGED Noesis RenderDevice (the "Extend"
/// path bank-heist's design doc flagged as having zero public reference implementations):
/// no GPU behind it — every callback is recorded, buffers are pinned managed arrays, textures
/// are inert wrappers. If Noesis calls DrawBatch here with coherent data, the managed route
/// is viable and the log is the exact spec (shaders, vertex formats, uniforms, textures) a
/// real WebGPU device must implement.</summary>
internal sealed class LoggingRenderDevice : RenderDevice
{
    public readonly List<string> Log = new();
    public readonly Dictionary<int, string> ShadersHit = new();

    private readonly byte[] _vertices = new byte[4 * 1024 * 1024];
    private readonly byte[] _indices = new byte[1024 * 1024];
    private GCHandle _vertexPin;
    private GCHandle _indexPin;

    public LoggingRenderDevice()
    {
        _vertexPin = GCHandle.Alloc(_vertices, GCHandleType.Pinned);
        _indexPin = GCHandle.Alloc(_indices, GCHandleType.Pinned);
    }

    private sealed class InertTexture : Texture
    {
        public InertTexture(uint width, uint height, TextureFormat format)
        {
            W = width; H = height; Format = format;
        }

        public uint W { get; }
        public uint H { get; }
        public TextureFormat Format { get; }
        public override uint Width => W;
        public override uint Height => H;
        public override bool HasMipMaps => false;
        public override bool IsInverted => false;
        public override bool HasAlpha => Format != TextureFormat.RGBX8;
    }

    private sealed class InertRenderTarget : RenderTarget
    {
        private readonly InertTexture _texture;
        public InertRenderTarget(uint width, uint height) => _texture = new InertTexture(width, height, TextureFormat.RGBA8);
        public override Texture Texture => _texture;
    }

    public override DeviceCaps Caps => new()
    {
        CenterPixelOffset = 0f,
        LinearRendering = false,
        SubpixelRendering = false,
        DepthRangeZeroToOne = true,   // WebGPU convention
        ClipSpaceYInverted = false,   // WebGPU: +Y up in clip space
    };

    public override RenderTarget CreateRenderTarget(string label, uint width, uint height, uint sampleCount, bool needsPingPong)
    {
        Log.Add($"CreateRenderTarget '{label}' {width}x{height} samples={sampleCount} pingPong={needsPingPong}");
        return new InertRenderTarget(width, height);
    }

    public override RenderTarget CloneRenderTarget(string label, RenderTarget surface)
    {
        Log.Add($"CloneRenderTarget '{label}'");
        var source = (InertRenderTarget)surface;
        return new InertRenderTarget(source.Texture.Width, source.Texture.Height);
    }

    public override void SetRenderTarget(RenderTarget surface) => Log.Add("SetRenderTarget");
    public override void BeginTile(RenderTarget surface, Tile tile) => Log.Add($"BeginTile {tile.Width}x{tile.Height}@{tile.X},{tile.Y}");
    public override void EndTile(RenderTarget surface) => Log.Add("EndTile");
    public override void ResolveRenderTarget(RenderTarget surface, Tile[] tiles) => Log.Add($"ResolveRenderTarget tiles={tiles.Length}");

    public override Texture CreateTexture(string label, uint width, uint height, uint numLevels, TextureFormat format, IntPtr data)
    {
        Log.Add($"CreateTexture '{label}' {width}x{height} levels={numLevels} format={format} initData={(data != IntPtr.Zero ? "yes" : "no")}");
        return new InertTexture(width, height, format);
    }

    public override void UpdateTexture(Texture texture, uint level, uint x, uint y, uint width, uint height, IntPtr data)
        => Log.Add($"UpdateTexture level={level} {width}x{height}@{x},{y}");

    public override void BeginOffscreenRender() => Log.Add("BeginOffscreenRender");
    public override void EndOffscreenRender() => Log.Add("EndOffscreenRender");
    public override void BeginOnscreenRender() => Log.Add("BeginOnscreenRender");
    public override void EndOnscreenRender() => Log.Add("EndOnscreenRender");

    public override IntPtr MapVertices(uint bytes)
    {
        Log.Add($"MapVertices {bytes}B");
        return _vertexPin.AddrOfPinnedObject();
    }

    public override void UnmapVertices() => Log.Add("UnmapVertices");

    public override IntPtr MapIndices(uint bytes)
    {
        Log.Add($"MapIndices {bytes}B");
        return _indexPin.AddrOfPinnedObject();
    }

    public override void UnmapIndices() => Log.Add("UnmapIndices");

    public override void DrawBatch(ref Batch batch)
    {
        ShadersHit[batch.Shader.Index] = batch.Shader.Name;
        Log.Add(
            $"DrawBatch shader={batch.Shader.Name}(#{batch.Shader.Index}) " +
            $"verts={batch.NumVertices}@{batch.VertexOffset} indices={batch.NumIndices}@{batch.StartIndex} " +
            $"stencilRef={batch.StencilRef} " +
            $"vsU0={batch.VertexUniform0.NumWords}w vsU1={batch.VertexUniform1.NumWords}w " +
            $"psU0={batch.PixelUniform0.NumWords}w psU1={batch.PixelUniform1.NumWords}w " +
            $"tex[pattern={(batch.Pattern != null ? "y" : "-")} ramps={(batch.Ramps != null ? "y" : "-")} " +
            $"image={(batch.Image != null ? "y" : "-")} glyphs={(batch.Glyphs != null ? "y" : "-")} shadow={(batch.Shadow != null ? "y" : "-")}]");
    }
}
