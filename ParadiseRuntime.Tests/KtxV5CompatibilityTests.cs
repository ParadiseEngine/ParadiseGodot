using System.Text.Json.Nodes;
using Paradise.Assets.Textures;
using ParadiseExport.Pipeline;

namespace ParadiseRuntime.Tests;

/// <summary>KTX-Software v5 (`ktx create`) output must stay decodable by the ENGINE's
/// transcoder (Ktx2.NET over libktx 4.x): encode a PNG through the full export-side GLB pass
/// with the vendored v5 CLI, then transcode the produced KTX2 back to RGBA32.</summary>
public class KtxV5CompatibilityTests
{
    // 8x8 transparent RGBA PNG (same fixture as ParadiseExport.Tests.PipelineTests).
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAADUlEQVR4nGNgGAUgAAABCAABgukLHQAAAABJRU5ErkJggg==";

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "third_party")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName;
    }

    [Test]
    public async Task v5_encoded_ktx2_transcodes_with_the_engine_transcoder()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot is null || KtxCreate.FindKtx(repoRoot) is null)
        {
            Skip.Test("ktx (KTX-Software v5) not available — vendored tool missing on this platform.");
        }

        byte[] png = Convert.FromBase64String(TinyPngBase64);
        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["images"] = new JsonArray(new JsonObject
            {
                ["name"] = "Wall_Albedo",
                ["mimeType"] = "image/png",
                ["bufferView"] = 0,
            }),
            ["textures"] = new JsonArray(new JsonObject { ["source"] = 0 }),
            ["bufferViews"] = new JsonArray(new JsonObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = 0,
                ["byteLength"] = png.Length,
            }),
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = png.Length }),
        };

        var path = Path.Combine(Path.GetTempPath(), $"paradise_ktx5_compat_{Guid.NewGuid():N}.glb");
        try
        {
            GlbBinary.Write(path, gltf, png);
            var result = KtxCreate.ConvertEmbeddedTextures(path, repoRoot);
            await Assert.That(result).IsEqualTo(KtxCreate.ConversionResult.ConvertedAllTextures);

            await Assert.That(GlbBinary.TryRead(path, out JsonObject converted, out byte[] bin)).IsTrue();
            var image = (JsonObject)converted["images"]![0]!;
            var view = (JsonObject)converted["bufferViews"]![(int)image["bufferView"]!.GetValue<int>()]!;
            var offset = (int?)view["byteOffset"] ?? 0;
            var length = (int)view["byteLength"]!.GetValue<int>();
            var ktx2 = bin.AsSpan(offset, length).ToArray();

            await Assert.That(Ktx2Transcoder.IsKtx2(ktx2)).IsTrue();
            CompressedTextureData decoded;
            try
            {
                decoded = Ktx2Transcoder.TranscodeToRgba32(ktx2, CompressedTextureUsage.ColorSrgb);
            }
            catch (DllNotFoundException)
            {
                Skip.Test("libktx native library not available on this platform.");
                return;
            }

            await Assert.That(decoded.IsEmpty).IsFalse();
            await Assert.That(decoded.Width).IsEqualTo(8);
            await Assert.That(decoded.Height).IsEqualTo(8);
            await Assert.That(decoded.MipLevels.Length).IsGreaterThanOrEqualTo(1);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
