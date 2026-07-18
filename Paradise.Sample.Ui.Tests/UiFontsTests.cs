using System.Buffers.Binary;

namespace Paradise.Sample.Ui.Tests;

/// <summary>The TrueType sniffer that keeps CFF/OpenType fonts away from stb_truetype
/// (which asserts in native code on them) — synthetic containers cover every branch.</summary>
public class UiFontsTests
{
    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uifonts_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Ttc(uint firstFaceTag)
    {
        // 'ttcf' header: tag, version, numFonts, offset[0] -> a face at byte 16.
        var bytes = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), 0x74746366);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), firstFaceTag);
        return bytes;
    }

    [Test]
    [Arguments(0x00010000u, true)]  // classic sfnt TrueType
    [Arguments(0x74727565u, true)]  // 'true' (Apple TrueType)
    [Arguments(0x4F54544Fu, false)] // 'OTTO' — CFF outlines, stb_truetype would assert
    [Arguments(0xDEADBEEFu, false)] // garbage
    public async Task plain_font_files_are_sniffed_by_sfnt_tag(uint tag, bool expected)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, tag);
        var path = WriteTemp(bytes);
        try
        {
            await Assert.That(UiFonts.IsStbLoadableTrueType(path)).IsEqualTo(expected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ttc_collections_are_sniffed_by_their_first_face()
    {
        var trueTypeTtc = WriteTemp(Ttc(0x00010000));
        var cffTtc = WriteTemp(Ttc(0x4F54544F)); // e.g. Hiragino Sans GB
        try
        {
            await Assert.That(UiFonts.IsStbLoadableTrueType(trueTypeTtc)).IsTrue();
            await Assert.That(UiFonts.IsStbLoadableTrueType(cffTtc)).IsFalse();
        }
        finally
        {
            File.Delete(trueTypeTtc);
            File.Delete(cffTtc);
        }
    }

    [Test]
    public async Task missing_and_truncated_files_are_rejected()
    {
        await Assert.That(UiFonts.IsStbLoadableTrueType("/nonexistent/font.ttf")).IsFalse();

        var tiny = WriteTemp([0x00, 0x01]);
        try
        {
            await Assert.That(UiFonts.IsStbLoadableTrueType(tiny)).IsFalse();
        }
        finally
        {
            File.Delete(tiny);
        }
    }

    [Test]
    public async Task system_probe_only_ever_returns_a_loadable_font()
    {
        // Platform-dependent whether one exists; the contract is it never returns junk.
        var found = UiFonts.FindSystemCjkFont();
        if (found is not null)
        {
            await Assert.That(UiFonts.IsStbLoadableTrueType(found)).IsTrue();
        }
    }
}
