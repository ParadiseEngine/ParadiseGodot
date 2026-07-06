#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ParadiseExport.Pipeline
{
    /// <summary>
    /// Converts the PNG/JPEG textures embedded in a GLB to KTX2 (Basis Universal) via the Khronos
    /// <c>toktx</c> CLI, rewriting the GLB to reference them through <c>KHR_texture_basisu</c>.
    /// Engine-neutral port of the Unity GlbKtx2TextureProcessor core. Resolves <c>toktx</c> from
    /// <c>PARADISE_TOKTX_PATH</c>, a repo-local <c>third_party/tools/KTX-Software</c>, or PATH; when
    /// unavailable the conversion fails gracefully and the GLB is left as-is.
    /// </summary>
    public static class ToktxKtx2
    {
        public const string ToktxPathEnvironmentVariable = "PARADISE_TOKTX_PATH";
        private const string Ktx2MimeType = "image/ktx2";
        private const string Ktx2ExtensionName = "KHR_texture_basisu";
        private const int ToktxTimeoutMilliseconds = 30 * 60 * 1000;

        public enum TextureEncodingPreset
        {
            BasisLzSrgb,
            BasisLzLinear,
            UastcDataLinear,
            UastcNormalLinear,
        }

        public enum ConversionResult
        {
            NoConvertibleTextures,
            ConvertedAllTextures,
            ToolMissing,
            Failed,
        }

        public static ConversionResult ConvertEmbeddedTextures(
            string glbFullPath,
            string? repoRoot = null,
            string? externalTextureRoot = null,
            Action<string>? log = null,
            Action<string>? error = null)
        {
            if (!File.Exists(glbFullPath) || !GlbBinary.TryRead(glbFullPath, out JsonObject gltf, out byte[] binChunk))
            {
                error?.Invoke($"Failed to parse GLB '{glbFullPath}'.");
                return ConversionResult.Failed;
            }

            if (gltf["images"] is not JsonArray images || gltf["textures"] is not JsonArray textures ||
                gltf["bufferViews"] is not JsonArray bufferViews)
            {
                return ConversionResult.NoConvertibleTextures;
            }

            // Resolve the tool only once the GLB is known to embed convertible images —
            // textureless meshes must not fail on a missing encoder (ToolMissing is now a
            // meaningful signal: "textures exist and could not be converted").
            if (!HasConvertibleImages(images))
            {
                return ConversionResult.NoConvertibleTextures;
            }

            string? toktxPath = FindToktx(repoRoot);
            if (string.IsNullOrWhiteSpace(toktxPath))
            {
                error?.Invoke(
                    $"toktx not found. Set {ToktxPathEnvironmentVariable}, vendor KTX-Software under third_party/tools/KTX-Software, or add toktx to PATH.");
                return ConversionResult.ToolMissing;
            }

            var convertedImageIndices = new HashSet<int>();
            var bufferViewReplacements = new Dictionary<int, byte[]>();
            int convertibleImageCount = 0;
            Dictionary<int, TextureEncodingPreset> presets = GetImageEncodingPresets(gltf, textures, images);

            foreach (JsonObject image in images.OfType<JsonObject>())
            {
                int sourceImageIndex = images.IndexOf(image);
                string mimeType = image["mimeType"]?.GetValue<string>() ?? "";
                if (!IsPngOrJpeg(mimeType) || image["bufferView"] == null)
                {
                    continue;
                }

                convertibleImageCount++;
                if (!TryGetSourceImageBytes(image, bufferViews, binChunk, externalTextureRoot, out byte[] sourceBytes, out int sourceBufferViewIndex))
                {
                    error?.Invoke($"Could not read texture #{sourceImageIndex} in '{glbFullPath}'.");
                    continue;
                }

                string sourceExtension = string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
                TextureEncodingPreset preset = presets.TryGetValue(sourceImageIndex, out TextureEncodingPreset matched)
                    ? matched
                    : PresetFromImageName(image);

                if (!TryConvertImageBytes(toktxPath, sourceBytes, sourceExtension, preset, out byte[] ktx2Bytes, error))
                {
                    continue;
                }

                bufferViewReplacements[sourceBufferViewIndex] = ktx2Bytes;
                image["name"] = Ktx2ImageName(image, sourceImageIndex);
                image["mimeType"] = Ktx2MimeType;
                image.Remove("uri");
                convertedImageIndices.Add(sourceImageIndex);
            }

            if (convertedImageIndices.Count == 0)
            {
                return convertibleImageCount > 0 ? ConversionResult.Failed : ConversionResult.NoConvertibleTextures;
            }

            if (convertedImageIndices.Count != convertibleImageCount)
            {
                error?.Invoke(
                    $"Converted {convertedImageIndices.Count} of {convertibleImageCount} textures in '{glbFullPath}'; GLB not rewritten.");
                return ConversionResult.Failed;
            }

            binChunk = RebuildBinaryChunk(bufferViews, binChunk, bufferViewReplacements);
            ApplyBasisTextureExtensions(gltf, textures, convertedImageIndices);
            UpdateFirstBufferLength(gltf, binChunk.Length);
            GlbBinary.Write(glbFullPath, gltf, binChunk);
            log?.Invoke($"Converted {convertedImageIndices.Count} embedded texture(s) in '{glbFullPath}' to KTX2.");
            return ConversionResult.ConvertedAllTextures;
        }

        // ---- toktx invocation -------------------------------------------------------------------

        public static string BuildToktxArguments(TextureEncodingPreset preset, string outputPath, string sourcePath)
        {
            var arguments = new List<string> { "--t2", "--upper_left_maps_to_s0t0", "--genmipmap" };

            switch (preset)
            {
                case TextureEncodingPreset.UastcNormalLinear:
                    arguments.AddRange(new[] { "--assign_oetf", "linear", "--normal_mode", "--encode", "uastc", "--uastc_quality", "2", "--zcmp", "10" });
                    break;
                case TextureEncodingPreset.UastcDataLinear:
                    arguments.AddRange(new[] { "--assign_oetf", "linear", "--encode", "uastc", "--uastc_quality", "2", "--zcmp", "10" });
                    break;
                case TextureEncodingPreset.BasisLzLinear:
                    arguments.AddRange(new[] { "--assign_oetf", "linear", "--encode", "etc1s", "--clevel", "5", "--qlevel", "255" });
                    break;
                default:
                    arguments.AddRange(new[] { "--assign_oetf", "srgb", "--encode", "etc1s", "--clevel", "5", "--qlevel", "255" });
                    break;
            }

            arguments.Add(ProcessTools.QuoteArgument(outputPath));
            arguments.Add(ProcessTools.QuoteArgument(sourcePath));
            return string.Join(" ", arguments);
        }

        public static bool IsValidKtx2(byte[] bytes, out string error)
        {
            error = "";
            if (bytes.Length < 80)
            {
                error = $"file is too small ({bytes.Length} bytes).";
                return false;
            }

            ReadOnlySpan<byte> identifier = stackalloc byte[]
            {
                0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A,
            };
            if (!bytes.AsSpan(0, identifier.Length).SequenceEqual(identifier))
            {
                error = "missing KTX2 identifier.";
                return false;
            }

            uint pixelWidth = BitConverter.ToUInt32(bytes, 20);
            uint pixelHeight = BitConverter.ToUInt32(bytes, 24);
            uint levelCount = BitConverter.ToUInt32(bytes, 40);
            if (pixelWidth == 0 || pixelHeight == 0)
            {
                error = $"invalid dimensions {pixelWidth}x{pixelHeight}.";
                return false;
            }

            if (levelCount == 0)
            {
                error = "missing mip levels.";
                return false;
            }

            return true;
        }

        private static bool TryConvertImageBytes(
            string toktxPath,
            byte[] sourceBytes,
            string sourceExtension,
            TextureEncodingPreset preset,
            out byte[] ktx2Bytes,
            Action<string>? error)
        {
            ktx2Bytes = Array.Empty<byte>();
            string tempDirectory = Path.Combine(Path.GetTempPath(), "ParadiseKtx2", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                string sourcePath = Path.Combine(tempDirectory, "source" + sourceExtension);
                string outputPath = Path.Combine(tempDirectory, "texture.ktx2");
                File.WriteAllBytes(sourcePath, sourceBytes);

                ProcessTools.ProcessResult run = ProcessTools.Run(
                    toktxPath,
                    BuildToktxArguments(preset, outputPath, sourcePath),
                    ToktxTimeoutMilliseconds,
                    ToktxEnvironment(toktxPath));

                if (!run.Succeeded || !File.Exists(outputPath))
                {
                    error?.Invoke($"toktx failed (code {run.ExitCode}).\n{run.Stdout}{run.Stderr}");
                    return false;
                }

                ktx2Bytes = File.ReadAllBytes(outputPath);
                if (!IsValidKtx2(ktx2Bytes, out string validationError))
                {
                    error?.Invoke($"toktx produced an invalid KTX2 texture: {validationError}");
                    ktx2Bytes = Array.Empty<byte>();
                    return false;
                }

                return true;
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        // On macOS, point the dynamic loader at toktx's bundled libs.
        private static IReadOnlyDictionary<string, string>? ToktxEnvironment(string toktxPath)
        {
            if (!OperatingSystem.IsMacOS())
            {
                return null;
            }

            string? toktxDirectory = Path.GetDirectoryName(toktxPath);
            if (string.IsNullOrWhiteSpace(toktxDirectory))
            {
                return null;
            }

            string libDirectory = Path.GetFullPath(Path.Combine(toktxDirectory, "..", "lib"));
            if (!Directory.Exists(libDirectory))
            {
                return null;
            }

            var env = new Dictionary<string, string>();
            foreach (string variable in new[] { "DYLD_LIBRARY_PATH", "DYLD_FALLBACK_LIBRARY_PATH" })
            {
                string? existing = Environment.GetEnvironmentVariable(variable);
                env[variable] = string.IsNullOrWhiteSpace(existing) ? libDirectory : libDirectory + Path.PathSeparator + existing;
            }

            return env;
        }

        // ---- preset selection -------------------------------------------------------------------

        private static Dictionary<int, TextureEncodingPreset> GetImageEncodingPresets(JsonObject gltf, JsonArray textures, JsonArray images)
        {
            var presets = new Dictionary<int, TextureEncodingPreset>();
            if (gltf["materials"] is not JsonArray materials)
            {
                return presets;
            }

            foreach (JsonObject material in materials.OfType<JsonObject>())
            {
                var pbr = material["pbrMetallicRoughness"] as JsonObject;
                ApplyTexturePreset(pbr?["baseColorTexture"], textures, images, TextureEncodingPreset.BasisLzSrgb, presets);
                ApplyTexturePreset(material["emissiveTexture"], textures, images, TextureEncodingPreset.BasisLzSrgb, presets);
                ApplyTexturePreset(pbr?["metallicRoughnessTexture"], textures, images, TextureEncodingPreset.UastcDataLinear, presets);
                ApplyTexturePreset(material["normalTexture"], textures, images, TextureEncodingPreset.UastcNormalLinear, presets);
                ApplyTexturePreset(material["occlusionTexture"], textures, images, TextureEncodingPreset.UastcDataLinear, presets);
            }

            return presets;
        }

        private static void ApplyTexturePreset(JsonNode? textureInfo, JsonArray textures, JsonArray images, TextureEncodingPreset preset, Dictionary<int, TextureEncodingPreset> presets)
        {
            int? textureIndex = textureInfo?["index"]?.GetValue<int>();
            if (textureIndex == null || textureIndex.Value < 0 || textureIndex.Value >= textures.Count)
            {
                return;
            }

            if (textures[textureIndex.Value] is not JsonObject texture)
            {
                return;
            }

            int? imageIndex = texture["source"]?.GetValue<int>();
            if (imageIndex == null || imageIndex.Value < 0 || imageIndex.Value >= images.Count)
            {
                return;
            }

            presets[imageIndex.Value] = MergeEncodingPreset(
                presets.TryGetValue(imageIndex.Value, out TextureEncodingPreset existing) ? existing : TextureEncodingPreset.BasisLzSrgb,
                preset);
        }

        private static TextureEncodingPreset MergeEncodingPreset(TextureEncodingPreset existing, TextureEncodingPreset next)
        {
            if (existing == TextureEncodingPreset.UastcNormalLinear || next == TextureEncodingPreset.UastcNormalLinear)
            {
                return TextureEncodingPreset.UastcNormalLinear;
            }

            if (existing == TextureEncodingPreset.UastcDataLinear || next == TextureEncodingPreset.UastcDataLinear)
            {
                return TextureEncodingPreset.UastcDataLinear;
            }

            if (existing == TextureEncodingPreset.BasisLzLinear || next == TextureEncodingPreset.BasisLzLinear)
            {
                return TextureEncodingPreset.BasisLzLinear;
            }

            return TextureEncodingPreset.BasisLzSrgb;
        }

        public static TextureEncodingPreset PresetFromImageName(JsonObject image)
        {
            string imageName = image["name"]?.GetValue<string>() ?? "";
            if (ContainsAny(imageName, "Normal", "NormalMap", "Bump"))
            {
                return TextureEncodingPreset.UastcNormalLinear;
            }

            if (ContainsAny(imageName, "Metallic", "Metalness", "Roughness", "Gloss", "Occlusion", "AO"))
            {
                return TextureEncodingPreset.UastcDataLinear;
            }

            if (ContainsAny(imageName, "Mask", "Height", "Displacement"))
            {
                return TextureEncodingPreset.BasisLzLinear;
            }

            return TextureEncodingPreset.BasisLzSrgb;
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            foreach (string needle in needles)
            {
                if (value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        // ---- GLB rewrite ------------------------------------------------------------------------

        private static bool TryGetSourceImageBytes(JsonObject image, JsonArray bufferViews, byte[] binChunk, string? externalTextureRoot, out byte[] bytes, out int sourceBufferViewIndex)
        {
            bytes = Array.Empty<byte>();
            sourceBufferViewIndex = -1;

            string? uri = image["uri"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(uri) && TryGetExternalImageBytes(uri, externalTextureRoot, out bytes))
            {
                sourceBufferViewIndex = image["bufferView"]!.GetValue<int>();
                return true;
            }

            if (image["bufferView"] == null)
            {
                return false;
            }

            int bufferViewIndex = image["bufferView"]!.GetValue<int>();
            if (!TryGetBufferViewBytes(bufferViews, bufferViewIndex, binChunk, out bytes))
            {
                return false;
            }

            sourceBufferViewIndex = bufferViewIndex;
            return true;
        }

        private static bool TryGetExternalImageBytes(string uri, string? externalTextureRoot, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrWhiteSpace(externalTextureRoot) ||
                uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                Uri.TryCreate(uri, UriKind.Absolute, out _))
            {
                return false;
            }

            string normalizedUri = Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(externalTextureRoot, normalizedUri));
            string fullRoot = Path.GetFullPath(externalTextureRoot);
            if (!fullPath.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath))
            {
                return false;
            }

            bytes = File.ReadAllBytes(fullPath);
            return true;
        }

        private static bool TryGetBufferViewBytes(JsonArray bufferViews, int bufferViewIndex, byte[] binChunk, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (bufferViewIndex < 0 || bufferViewIndex >= bufferViews.Count)
            {
                return false;
            }

            if (bufferViews[bufferViewIndex] is not JsonObject bufferView || (bufferView["buffer"]?.GetValue<int>() ?? 0) != 0)
            {
                return false;
            }

            int byteOffset = bufferView["byteOffset"]?.GetValue<int>() ?? 0;
            int byteLength = bufferView["byteLength"]?.GetValue<int>() ?? 0;
            if (byteOffset < 0 || byteLength <= 0 || byteOffset + byteLength > binChunk.Length)
            {
                return false;
            }

            bytes = new byte[byteLength];
            Array.Copy(binChunk, byteOffset, bytes, 0, byteLength);
            return true;
        }

        private static byte[] RebuildBinaryChunk(JsonArray bufferViews, byte[] sourceBinChunk, IReadOnlyDictionary<int, byte[]> replacements)
        {
            using var rebuilt = new MemoryStream();
            for (int i = 0; i < bufferViews.Count; i++)
            {
                if (bufferViews[i] is not JsonObject bufferView || (bufferView["buffer"]?.GetValue<int>() ?? 0) != 0)
                {
                    continue;
                }

                int sourceOffset = bufferView["byteOffset"]?.GetValue<int>() ?? 0;
                int sourceLength = bufferView["byteLength"]?.GetValue<int>() ?? 0;
                if (sourceOffset < 0 || sourceLength <= 0 || sourceOffset + sourceLength > sourceBinChunk.Length)
                {
                    continue;
                }

                GlbBinary.WritePadding(rebuilt, 0x00);
                byte[] bytes = replacements.TryGetValue(i, out byte[]? replacement)
                    ? replacement
                    : CopyBytes(sourceBinChunk, sourceOffset, sourceLength);

                bufferView["byteOffset"] = (int)rebuilt.Position;
                bufferView["byteLength"] = bytes.Length;
                rebuilt.Write(bytes, 0, bytes.Length);
            }

            GlbBinary.WritePadding(rebuilt, 0x00);
            return rebuilt.ToArray();
        }

        private static void ApplyBasisTextureExtensions(JsonObject gltf, JsonArray textures, ISet<int> ktx2ImageIndices)
        {
            foreach (JsonObject texture in textures.OfType<JsonObject>())
            {
                if (texture["source"] == null)
                {
                    continue;
                }

                int source = texture["source"]!.GetValue<int>();
                if (!ktx2ImageIndices.Contains(source))
                {
                    continue;
                }

                if (texture["extensions"] is not JsonObject extensions)
                {
                    extensions = new JsonObject();
                    texture["extensions"] = extensions;
                }

                extensions[Ktx2ExtensionName] = new JsonObject { ["source"] = source };
                texture.Remove("source");
            }

            AddExtensionName(gltf, "extensionsUsed");
            AddExtensionName(gltf, "extensionsRequired");
        }

        private static void AddExtensionName(JsonObject gltf, string propertyName)
        {
            if (gltf[propertyName] is not JsonArray extensions)
            {
                extensions = new JsonArray();
                gltf[propertyName] = extensions;
            }

            // Match by value only on string entries — GetValue<string>() throws on non-string nodes,
            // and a malformed GLB may carry numeric/object entries in extensionsUsed/Required.
            if (!extensions.Any(n => n is JsonValue v && v.TryGetValue(out string? s)
                    && string.Equals(s, Ktx2ExtensionName, StringComparison.Ordinal)))
            {
                extensions.Add((JsonNode)Ktx2ExtensionName);
            }
        }

        private static void UpdateFirstBufferLength(JsonObject gltf, int byteLength)
        {
            var buffers = gltf["buffers"] as JsonArray;
            if (buffers?.Count > 0 && buffers[0] is JsonObject buffer)
            {
                buffer["byteLength"] = byteLength;
                return;
            }

            gltf["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = byteLength });
        }

        // ---- tool resolution --------------------------------------------------------------------

        public static string? FindToktx(string? repoRoot = null) =>
            ProcessTools.FindExecutable(
                Environment.GetEnvironmentVariable(ToktxPathEnvironmentVariable),
                RepositoryToktxPaths(repoRoot),
                "toktx");

        private static IEnumerable<string> RepositoryToktxPaths(string? repoRoot)
        {
            string root = Path.GetFullPath(Path.Combine(repoRoot ?? Directory.GetCurrentDirectory(), "third_party", "tools", "KTX-Software"));
            if (OperatingSystem.IsMacOS())
            {
                yield return Path.Combine(root, "Darwin-arm64", "bin", "toktx");
            }

            if (!Directory.Exists(root))
            {
                yield break;
            }

            string[] fileNames = OperatingSystem.IsWindows()
                ? new[] { "toktx.exe" }
                : OperatingSystem.IsMacOS()
                    ? new[] { "toktx" }
                    : new[] { "toktx", "toktx.exe" };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string fileName in fileNames)
            {
                foreach (string candidate in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
                {
                    if (seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
        }

        private static bool HasConvertibleImages(JsonArray images)
        {
            foreach (JsonObject image in images.OfType<JsonObject>())
            {
                string mimeType = image["mimeType"]?.GetValue<string>() ?? "";
                if (IsPngOrJpeg(mimeType) && image["bufferView"] != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPngOrJpeg(string mimeType) =>
            string.Equals(mimeType, "image/png", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase);

        private static string Ktx2ImageName(JsonObject sourceImage, int sourceImageIndex)
        {
            string sourceName = Path.GetFileNameWithoutExtension(sourceImage["name"]?.GetValue<string>() ?? $"Texture_{sourceImageIndex}");
            return $"{sourceName}_KTX2.ktx2";
        }

        private static byte[] CopyBytes(byte[] source, int offset, int length)
        {
            byte[] copy = new byte[length];
            Array.Copy(source, offset, copy, 0, length);
            return copy;
        }
    }
}
