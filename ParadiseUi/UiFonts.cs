using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace ParadiseUi;

/// <summary>Font selection for <see cref="ImGuiUiCore"/>. <paramref name="Path"/> empty/null
/// means "probe the platform's known CJK-capable system fonts".
/// <paramref name="GlyphSourceText"/> (e.g. the game's whole config JSON) guarantees every
/// authored character gets a glyph even outside the common-Chinese ranges — the static atlas
/// bakes exactly what the content needs.</summary>
public sealed record UiFontConfig(string? Path, float SizePixels, string? GlyphSourceText = null);

/// <summary>
/// CJK font resolution for the shared ImGui core. ImGui's default font is ASCII-only, so any
/// CJK text renders as '?'; loading a system font with the Chinese glyph ranges fixes that.
/// The catch: ImGui rasterizes with stb_truetype, which only parses TrueType ('glyf')
/// outlines — feeding it a CFF/OpenType font (e.g. Hiragino, Noto CJK OTC) asserts inside
/// native code. So candidates are sniffed by container magic first and CFF fonts are
/// skipped; a font that fails the sniff falls back to the next candidate, and no candidate
/// at all falls back to the default font (ASCII-only, the pre-CJK behavior).
/// </summary>
public static class UiFonts
{
    /// <summary>Well-known CJK-capable system fonts per platform, tried in order. All are
    /// verified TrueType by <see cref="IsStbLoadableTrueType"/> before use anyway.</summary>
    public static readonly string[] SystemCjkFontCandidates =
    [
        // macOS
        "/Library/Fonts/Arial Unicode.ttf",
        "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",
        "/System/Library/Fonts/STHeiti Light.ttc",
        "/System/Library/Fonts/STHeiti Medium.ttc",
        "/System/Library/Fonts/PingFang.ttc",
        // Windows
        @"C:\Windows\Fonts\msyh.ttc",
        @"C:\Windows\Fonts\simhei.ttf",
        @"C:\Windows\Fonts\simsun.ttc",
        // Linux
        "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc",
        "/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf",
    ];

    /// <summary>True when the file exists and its first face uses TrueType outlines that
    /// stb_truetype can parse (sfnt 0x00010000 / 'true'; for 'ttcf' collections the first
    /// face is checked). 'OTTO' (CFF) and anything unreadable is rejected.</summary>
    public static bool IsStbLoadableTrueType(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[16];
            if (stream.Read(header[..4]) != 4) return false;
            var tag = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);

            if (tag == 0x74746366) // 'ttcf' — check the first face's sfnt version
            {
                if (stream.Read(header[..12]) != 12) return false;
                var firstFaceOffset = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);
                stream.Position = firstFaceOffset;
                if (stream.Read(header[..4]) != 4) return false;
                tag = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            }

            return tag is 0x00010000 or 0x74727565; // sfnt v1 / 'true'
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The first stb-loadable CJK system font on this machine, or null.</summary>
    public static string? FindSystemCjkFont()
    {
        foreach (var candidate in SystemCjkFontCandidates)
        {
            if (File.Exists(candidate) && IsStbLoadableTrueType(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Add a CJK-capable font (explicit path or system probe) to the atlas with the
    /// common-simplified-Chinese glyph ranges (ASCII + punctuation + ~2500 common hanzi —
    /// the full ideograph set would blow the static atlas up for little gain; rare glyphs
    /// still render as '?'). False = nothing added, caller falls back to the default font.</summary>
    internal static bool TryAddCjkFont(ImGuiIOPtr io, UiFontConfig font)
    {
        var path = string.IsNullOrWhiteSpace(font.Path) ? FindSystemCjkFont() : font.Path;
        if (path is null)
        {
            Console.WriteLine("[ImGuiUi] no CJK-capable system font found — falling back to the ASCII default font.");
            return false;
        }
        if (!File.Exists(path) || !IsStbLoadableTrueType(path))
        {
            Console.WriteLine($"[ImGuiUi] font '{path}' missing or not stb-loadable TrueType — falling back to the ASCII default font.");
            return false;
        }

        var ranges = io.Fonts.GetGlyphRangesChineseSimplifiedCommon();
        if (!string.IsNullOrEmpty(font.GlyphSourceText))
        {
            ranges = BuildRangesWithSourceText(io, font.GlyphSourceText);
        }
        io.Fonts.AddFontFromFileTTF(path, font.SizePixels, default, ranges);
        Console.WriteLine($"[ImGuiUi] CJK font: {path} @ {font.SizePixels}px.");
        return true;
    }

    // Pinned for the process lifetime — the atlas reads the ranges during Build(), and the
    // font context is process-scoped anyway (one ImGui context per process).
    private static readonly List<GCHandle> PinnedRanges = new();

    /// <summary>Common-Chinese ranges UNION every character of the authored content, via
    /// ImGui's glyph-ranges builder — rare hanzi in names/flavor text stay renderable.</summary>
    private static unsafe nint BuildRangesWithSourceText(ImGuiIOPtr io, string sourceText)
    {
        var builder = new ImFontGlyphRangesBuilderPtr(ImGuiNative.ImFontGlyphRangesBuilder_ImFontGlyphRangesBuilder());
        try
        {
            builder.AddRanges(io.Fonts.GetGlyphRangesChineseSimplifiedCommon());
            builder.AddText(sourceText);
            builder.BuildRanges(out var vector);
            var copy = new ushort[vector.Size + 1]; // keep the 0 terminator
            for (var i = 0; i < vector.Size; i++)
            {
                copy[i] = ((ushort*)vector.Data)[i];
            }
            var handle = GCHandle.Alloc(copy, GCHandleType.Pinned);
            PinnedRanges.Add(handle);
            return handle.AddrOfPinnedObject();
        }
        finally
        {
            builder.Destroy();
        }
    }
}
