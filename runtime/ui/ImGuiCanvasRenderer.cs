using System;
using System.Collections.Generic;
using Godot;
using Paradise.Ui.ImGui;

namespace ParadiseGodot.Runtime.Ui;

/// <summary>The Godot render half of Dear ImGui: replays <see cref="ImGuiDrawSnapshot"/>s
/// (produced on the sim thread by the engine's <see cref="ImGuiUiCore"/>) as pooled
/// RenderingServer canvas items — one item per draw command so each gets its own scissor
/// rect via canvas-item clipping. RenderingServer-only (no Control nodes), so the overlay
/// never intercepts Godot input; UI-consumes-click arbitration happens on the sim thread
/// through <c>SimulationRunner.PumpUi</c>.
///
/// Textures arrive through ImGui's 1.92 protocol as <see cref="ImGuiTextureOp"/>s rather than
/// as one atlas copied at construction: the font atlas is rasterized on demand, so a glyph
/// first drawn mid-session reaches this side as an Update op. Ops are applied every frame,
/// before the snapshot they precede is replayed, whether or not that snapshot is new.
///
/// Coordinates are logical viewport space end-to-end (project stretch mode `canvas_items`):
/// the snapshot's DisplaySize equals the visible rect, and Godot's stretch transform handles
/// physical scaling — FramebufferScale is deliberately ignored. ImGui vertex colors are
/// straight-alpha, so the default Mix canvas blend is correct.</summary>
public sealed partial class ImGuiCanvasRenderer : Node2D
{
    /// <summary>How many frames a destroyed texture stays drawable. Retained canvas items from
    /// the last rebuilt snapshot may still name it, and a freed RID draws nothing without
    /// saying why — the same window the engine's WebGPU renderer waits out.</summary>
    private const int DestroyDelayFrames = 3;

    private ImGuiUiCore _core = null!;
    private readonly List<ImGuiTextureOp> _textureOps = new();
    private readonly Dictionary<ulong, ImGuiTexture> _textures = new();
    private readonly List<(ulong Id, ImGuiTexture Texture, int FramesLeft)> _retiring = new();
    private readonly List<Rid> _pool = new(); // one canvas item per draw command, reused
    private int _visibleItems;

    // Pooled decode arrays (grow-only, like the snapshot's own buffers).
    private Vector2[] _points = [];
    private Vector2[] _uvs = [];
    private Color[] _colors = [];
    private int[] _indices = [];

    /// <summary>An ImGui-owned texture: the CPU image is kept because Godot's
    /// <see cref="ImageTexture.Update"/> replaces the whole texture, so a sub-rectangle update
    /// is a blit into this image followed by a full upload.</summary>
    private sealed record ImGuiTexture(Image Image, ImageTexture Texture);

    public void Initialize(ImGuiUiCore core) => _core = core;

    public override void _Process(double delta)
    {
        var snapshot = _core.AcquireSnapshotForRender(_textureOps, out var isNew);
        ApplyTextureOps();
        if (snapshot is null || !isNew) return; // keep last frame's retained canvas items
        Rebuild(snapshot);
    }

    public override void _ExitTree()
    {
        foreach (var rid in _pool) RenderingServer.FreeRid(rid);
        _pool.Clear();
        foreach (var texture in _textures.Values) texture.Texture.Dispose();
        _textures.Clear();
        foreach (var retired in _retiring) retired.Texture.Texture.Dispose();
        _retiring.Clear();
    }

    /// <summary>Apply every pending op in order, then clear — the list is cleared only once all
    /// of them landed, so a throw part-way replays them next frame rather than losing a Create
    /// that a later Update depends on.</summary>
    private void ApplyTextureOps()
    {
        foreach (var op in _textureOps)
        {
            switch (op.Kind)
            {
                case ImGuiTextureOpKind.Create:
                    Retire(op.TextureId);
                    var image = Image.CreateFromData((int)op.Width, (int)op.Height, false, Image.Format.Rgba8, op.Pixels);
                    _textures[op.TextureId] = new ImGuiTexture(image, ImageTexture.CreateFromImage(image));
                    break;
                case ImGuiTextureOpKind.Update:
                    if (!_textures.TryGetValue(op.TextureId, out var target)) break;
                    var patch = Image.CreateFromData((int)op.Width, (int)op.Height, false, Image.Format.Rgba8, op.Pixels);
                    target.Image.BlitRect(patch, new Rect2I(0, 0, (int)op.Width, (int)op.Height), new Vector2I((int)op.X, (int)op.Y));
                    target.Texture.Update(target.Image);
                    break;
                case ImGuiTextureOpKind.Destroy:
                    Retire(op.TextureId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(op), op.Kind, "Unknown ImGui texture op.");
            }
        }
        _textureOps.Clear();
        AgeRetiredTextures();
    }

    private void Retire(ulong id)
    {
        if (_textures.Remove(id, out var texture))
        {
            _retiring.Add((id, texture, DestroyDelayFrames));
        }
    }

    private void AgeRetiredTextures()
    {
        for (var i = _retiring.Count - 1; i >= 0; i--)
        {
            var retired = _retiring[i];
            if (retired.FramesLeft > 1)
            {
                _retiring[i] = retired with { FramesLeft = retired.FramesLeft - 1 };
                continue;
            }
            retired.Texture.Texture.Dispose();
            _retiring.RemoveAt(i);
        }
    }

    private void Rebuild(ImGuiDrawSnapshot snapshot)
    {
        DecodeVertices(snapshot);
        var displayPos = snapshot.DisplayPosition;

        var used = 0;
        for (var i = 0; i < snapshot.CommandCount; i++)
        {
            ref readonly var cmd = ref snapshot.Commands[i];
            if (cmd.ElementCount == 0) continue;
            if (Texture(cmd.TextureId) is not { } texture) continue;

            var clipWidth = cmd.ClipRect.Z - cmd.ClipRect.X;
            var clipHeight = cmd.ClipRect.W - cmd.ClipRect.Y;
            if (clipWidth <= 0f || clipHeight <= 0f) continue;

            var item = RentItem(used++);
            RenderingServer.CanvasItemClear(item);
            RenderingServer.CanvasItemSetClip(item, true);
            RenderingServer.CanvasItemSetCustomRect(item, true, new Rect2(
                cmd.ClipRect.X - displayPos.X, cmd.ClipRect.Y - displayPos.Y, clipWidth, clipHeight));
            RenderingServer.CanvasItemSetDrawIndex(item, used - 1);

            // Rebase this command's ushort indices into the concatenated vertex stream.
            if (_indices.Length < cmd.ElementCount) _indices = new int[(int)cmd.ElementCount];
            for (var e = 0; e < cmd.ElementCount; e++)
            {
                var raw = BitConverter.ToUInt16(snapshot.Indices, ((int)cmd.IndexOffset + e) * 2);
                _indices[e] = raw + (int)cmd.VertexOffset;
            }

            RenderingServer.CanvasItemAddTriangleArray(
                item, _indices.AsSpan(0, (int)cmd.ElementCount).ToArray(), _points, _colors, _uvs,
                null, null, texture, -1);
        }

        // Hide surplus pooled items (previous frame had more commands).
        for (var i = used; i < _visibleItems; i++)
        {
            RenderingServer.CanvasItemClear(_pool[i]);
            RenderingServer.CanvasItemSetVisible(_pool[i], false);
        }
        _visibleItems = used;
    }

    /// <summary>The RID a command samples: live, or retired but still within its grace
    /// frames. Null skips the command — a snapshot can outlive the op that destroyed its
    /// texture, and drawing it untextured would be a white rectangle.</summary>
    private Rid? Texture(ulong id)
    {
        if (_textures.TryGetValue(id, out var live)) return live.Texture.GetRid();
        foreach (var retired in _retiring)
        {
            if (retired.Id == id) return retired.Texture.Texture.GetRid();
        }
        return null;
    }

    private Rid RentItem(int index)
    {
        if (index < _pool.Count)
        {
            RenderingServer.CanvasItemSetVisible(_pool[index], true);
            return _pool[index];
        }
        var item = RenderingServer.CanvasItemCreate();
        RenderingServer.CanvasItemSetParent(item, GetCanvasItem());
        _pool.Add(item);
        return item;
    }

    /// <summary>Decode the interleaved vertex stream (stride 20: pos 2×f32, uv 2×f32, col
    /// RGBA8) once per snapshot into the pooled full-stream arrays every command indexes.</summary>
    private void DecodeVertices(ImGuiDrawSnapshot snapshot)
    {
        var count = snapshot.VertexBytes / ImGuiDrawSnapshot.VertexStride;
        if (_points.Length < count)
        {
            _points = new Vector2[count];
            _uvs = new Vector2[count];
            _colors = new Color[count];
        }
        var bytes = snapshot.Vertices;
        for (var v = 0; v < count; v++)
        {
            var at = v * ImGuiDrawSnapshot.VertexStride;
            _points[v] = new Vector2(BitConverter.ToSingle(bytes, at), BitConverter.ToSingle(bytes, at + 4));
            _uvs[v] = new Vector2(BitConverter.ToSingle(bytes, at + 8), BitConverter.ToSingle(bytes, at + 12));
            _colors[v] = new Color(
                bytes[at + 16] / 255f, bytes[at + 17] / 255f, bytes[at + 18] / 255f, bytes[at + 19] / 255f);
        }
    }
}
