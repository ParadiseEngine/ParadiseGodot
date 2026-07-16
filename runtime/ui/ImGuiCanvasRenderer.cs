using System;
using System.Collections.Generic;
using Godot;
using Paradise.Ui.ImGui;
using ParadiseUi;

namespace ParadiseGodot.Runtime.Ui;

/// <summary>The Godot render half of Dear ImGui: replays <see cref="ImGuiDrawSnapshot"/>s
/// (produced on the sim thread by the shared <see cref="ImGuiUiCore"/>) as pooled
/// RenderingServer canvas items — one item per draw command so each gets its own scissor
/// rect via canvas-item clipping. RenderingServer-only (no Control nodes), so the overlay
/// never intercepts Godot input; UI-consumes-click arbitration happens on the sim thread
/// through <c>SimulationRunner.PumpUi</c>.
///
/// Coordinates are logical viewport space end-to-end (project stretch mode `canvas_items`):
/// the snapshot's DisplaySize equals the visible rect, and Godot's stretch transform handles
/// physical scaling — FramebufferScale is deliberately ignored. ImGui vertex colors are
/// straight-alpha, so the default Mix canvas blend is correct.</summary>
public sealed partial class ImGuiCanvasRenderer : Node2D
{
    private ImGuiUiCore _core = null!;
    private ImageTexture _fontTexture = null!;
    private readonly Dictionary<nint, Rid> _textures = new();
    private readonly List<Rid> _pool = new(); // one canvas item per draw command, reused
    private int _visibleItems;

    // Pooled decode arrays (grow-only, like the snapshot's own buffers).
    private Vector2[] _points = [];
    private Vector2[] _uvs = [];
    private Color[] _colors = [];
    private int[] _indices = [];

    public void Initialize(ImGuiUiCore core)
    {
        _core = core;
        var image = Image.CreateFromData(
            (int)core.FontWidth, (int)core.FontHeight, false, Image.Format.Rgba8, core.FontPixels.ToArray());
        _fontTexture = ImageTexture.CreateFromImage(image);
        _textures[ImGuiUiCore.FontTextureId] = _fontTexture.GetRid();
    }

    public override void _Process(double delta)
    {
        var snapshot = _core.AcquireSnapshotForRender(out var isNew);
        if (snapshot is null || !isNew) return; // keep last frame's retained canvas items
        Rebuild(snapshot);
    }

    public override void _ExitTree()
    {
        foreach (var rid in _pool) RenderingServer.FreeRid(rid);
        _pool.Clear();
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

            var texture = _textures.TryGetValue(cmd.TextureId, out var rid) ? rid : _fontTexture.GetRid();
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
