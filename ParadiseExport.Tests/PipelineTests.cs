using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using ParadiseExport.Pipeline;

namespace ParadiseExport.Tests;

// Engine-neutral coverage of the asset-pipeline logic: GLB container round-trip, toktx argument
// building + preset selection, KTX2 validation, and executable resolution. No Blender/toktx needed.
public class PipelineTests
{
    [Test]
    public async Task glb_round_trips_json_and_bin()
    {
        var gltf = new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" }, ["meshes"] = new JsonArray() };
        byte[] bin = { 1, 2, 3, 4, 5 };
        string path = Path.Combine(Path.GetTempPath(), $"paradise_glb_{Guid.NewGuid():N}.glb");
        try
        {
            GlbBinary.Write(path, gltf, bin);
            bool read = GlbBinary.TryRead(path, out JsonObject readGltf, out byte[] readBin);

            await Assert.That(read).IsTrue();
            await Assert.That((string?)readGltf["asset"]!["version"]).IsEqualTo("2.0");
            // BIN chunk is padded to a 4-byte boundary; the original bytes are preserved as a prefix.
            await Assert.That(readBin.Length).IsGreaterThanOrEqualTo(bin.Length);
            await Assert.That(readBin[0]).IsEqualTo((byte)1);
            await Assert.That(readBin[4]).IsEqualTo((byte)5);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task toktx_args_srgb_default_vs_normal_preset()
    {
        string srgb = ToktxKtx2.BuildToktxArguments(ToktxKtx2.TextureEncodingPreset.BasisLzSrgb, "out.ktx2", "in.png");
        await Assert.That(srgb).Contains("--assign_oetf srgb");
        await Assert.That(srgb).Contains("--encode etc1s");
        await Assert.That(srgb).Contains("--genmipmap");

        string normal = ToktxKtx2.BuildToktxArguments(ToktxKtx2.TextureEncodingPreset.UastcNormalLinear, "out.ktx2", "in.png");
        await Assert.That(normal).Contains("--normal_mode");
        await Assert.That(normal).Contains("--encode uastc");
        await Assert.That(normal).Contains("--assign_oetf linear");
    }

    [Test]
    public async Task preset_inferred_from_image_name()
    {
        await Assert.That(ToktxKtx2.PresetFromImageName(new JsonObject { ["name"] = "Wall_Normal" }))
            .IsEqualTo(ToktxKtx2.TextureEncodingPreset.UastcNormalLinear);
        await Assert.That(ToktxKtx2.PresetFromImageName(new JsonObject { ["name"] = "Steel_Roughness" }))
            .IsEqualTo(ToktxKtx2.TextureEncodingPreset.UastcDataLinear);
        await Assert.That(ToktxKtx2.PresetFromImageName(new JsonObject { ["name"] = "Hero_Albedo" }))
            .IsEqualTo(ToktxKtx2.TextureEncodingPreset.BasisLzSrgb);
    }

    [Test]
    public async Task ktx2_validation_rejects_garbage_accepts_valid_header()
    {
        await Assert.That(ToktxKtx2.IsValidKtx2(new byte[10], out _)).IsFalse();

        byte[] valid = new byte[80];
        byte[] identifier = { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };
        Array.Copy(identifier, valid, identifier.Length);
        BitConverter.GetBytes(4u).CopyTo(valid, 20); // pixelWidth
        BitConverter.GetBytes(4u).CopyTo(valid, 24); // pixelHeight
        BitConverter.GetBytes(1u).CopyTo(valid, 40); // levelCount
        await Assert.That(ToktxKtx2.IsValidKtx2(valid, out _)).IsTrue();
    }

    [Test]
    public async Task quote_argument_handles_plain_and_trailing_backslash()
    {
        await Assert.That(ProcessTools.QuoteArgument("plain")).IsEqualTo("\"plain\"");
        // A trailing backslash must be doubled so it can't escape the closing quote on Windows.
        await Assert.That(ProcessTools.QuoteArgument(@"C:\dir\").EndsWith("\\\\\"")).IsTrue();
    }

    [Test]
    public async Task corrupt_glb_returns_false_instead_of_throwing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"paradise_bad_{Guid.NewGuid():N}.glb");
        File.WriteAllText(path, "not a glb");
        try
        {
            await Assert.That(GlbBinary.TryRead(path, out _, out _)).IsFalse();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task find_executable_prefers_env_path_when_present()
    {
        string fake = Path.Combine(Path.GetTempPath(), $"paradise_tool_{Guid.NewGuid():N}");
        File.WriteAllText(fake, "");
        try
        {
            await Assert.That(ProcessTools.FindExecutable(fake, Array.Empty<string>(), "does-not-exist-xyz"))
                .IsEqualTo(fake);
            await Assert.That(ProcessTools.FindExecutable(null, Array.Empty<string>(), "definitely-not-a-real-binary-xyz"))
                .IsNull();
        }
        finally
        {
            if (File.Exists(fake)) File.Delete(fake);
        }
    }
}
