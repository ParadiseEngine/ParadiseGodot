#if TOOLS
using System.Collections.Generic;
using System.IO;
using Godot;
using Paradise.Assets.Pipeline;

namespace ParadiseGodot.Pipeline
{
    /// <summary>
    /// Converts the embedded textures of GLBs living under <c>res://data/</c> to KTX2 (Basis
    /// Universal) IN PLACE via <see cref="KtxTool"/>. This is the ingest step of the
    /// source-GLB pipeline: the .NET runtime's GLB reader is KTX2-only, so any GLB it will load
    /// must have its PNG/JPEG textures transcoded once at import time (not baked per scene export).
    ///
    /// Idempotent: an already-KTX2 GLB yields <see cref="ConversionResult.NoConvertibleTextures"/>
    /// and is left untouched, so re-running (and the import hook's reimport) never loops.
    /// A missing <c>ktx</c> CLI is a WARNING here (not fatal): the GLB stays PNG and still renders
    /// in the Godot editor; only the .NET runtime needs the KTX2 form.
    /// </summary>
    internal static class DataGlbConverter
    {
        private static string DataDir => ParadisePaths.DataDir;

        /// <summary>Convert every <c>.glb</c>/<c>.gltf</c> under <c>res://data/</c>. Returns the
        /// number of GLBs that were rewritten to KTX2.</summary>
        public static int ConvertAll()
        {
            int converted = 0;
            var reimport = new List<string>();
            foreach (string resPath in FindDataGlbs(DataDir))
            {
                if (Convert(resPath, out bool rewritten) && rewritten)
                {
                    converted++;
                    reimport.Add(resPath);
                }
            }

            if (reimport.Count > 0)
            {
                EditorInterface.Singleton.GetResourceFilesystem().ReimportFiles(reimport.ToArray());
            }

            converted += ConvertSpriteSheets();

            GD.Print($"[Paradise.Export] data/ GLB KTX2 pass: {converted} converted.");
            return converted;
        }

        private static string SpritesDir => ParadisePaths.SpritesDir;

        /// <summary>Encode a KTX2 sidecar next to every spritesheet image under
        /// <c>res://data/sprites/</c> (the sheet convention the SpriteAnimation/ParticleEmitter
        /// contract components reference with a <c>.ktx2</c> extension). The Godot host keeps
        /// rendering the source image; only the .NET runtime reads the sidecar. Idempotent by
        /// timestamp (see <see cref="GlbTextureWorkflows.ConvertImageFile"/>).</summary>
        public static int ConvertSpriteSheets()
        {
            int converted = 0;
            foreach (string resPath in FindDataImages(SpritesDir))
            {
                string full = ProjectSettings.GlobalizePath(resPath);
                ConversionResult result = GlbTextureWorkflows.ConvertImageFile(
                    full,
                    Path.ChangeExtension(full, ".ktx2"),
                    repoRoot: ProjectSettings.GlobalizePath("res://"),
                    log: msg => GD.Print($"[Paradise.Assets] {msg}"),
                    error: msg => GD.PushError($"[Paradise.Assets] {msg}"));
                switch (result)
                {
                    case ConversionResult.ConvertedAllTextures:
                        converted++;
                        break;
                    case ConversionResult.ToolMissing:
                        GD.PushWarning(
                            $"[Paradise.Export] ktx (KTX-Software v5) not found — '{resPath}' has no KTX2 sidecar. " +
                            "The Godot editor renders the source image, but the .NET runtime needs the sidecar; set " +
                            "PARADISE_KTX_PATH or install KTX-Software, then re-run Paradise/Convert data GLBs → KTX2.");
                        return converted; // one warning is enough — the tool is missing for all of them
                }
            }

            return converted;
        }

        private static IEnumerable<string> FindDataImages(string dirResPath)
        {
            using var dir = DirAccess.Open(dirResPath);
            if (dir is null)
            {
                yield break;
            }

            dir.ListDirBegin();
            for (string entry = dir.GetNext(); !string.IsNullOrEmpty(entry); entry = dir.GetNext())
            {
                if (entry is "." or "..")
                {
                    continue;
                }

                string childResPath = $"{dirResPath}/{entry}";
                if (dir.CurrentIsDir())
                {
                    foreach (string nested in FindDataImages(childResPath))
                    {
                        yield return nested;
                    }
                }
                else if (entry.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) ||
                         entry.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase) ||
                         entry.EndsWith(".jpeg", System.StringComparison.OrdinalIgnoreCase))
                {
                    yield return childResPath;
                }
            }
        }

        /// <summary>Convert a single GLB in place. <paramref name="rewritten"/> is true only when
        /// the file was actually changed (textures transcoded) — the caller uses it to decide
        /// whether a reimport is needed. Returns false only on hard failure.</summary>
        public static bool Convert(string resPath, out bool rewritten)
        {
            rewritten = false;
            string full = ProjectSettings.GlobalizePath(resPath);
            ConversionResult result = GlbTextureWorkflows.ExternalizeTextures(
                full,
                repoRoot: ProjectSettings.GlobalizePath("res://"),
                log: msg => GD.Print($"[Paradise.Assets] {msg}"),
                error: msg => GD.PushError($"[Paradise.Assets] {msg}"));

            switch (result)
            {
                case ConversionResult.ConvertedAllTextures:
                    rewritten = true;
                    return true;
                case ConversionResult.NoConvertibleTextures:
                    return true; // already KTX2 / untextured — nothing to do (idempotent)
                case ConversionResult.ToolMissing:
                    GD.PushWarning(
                        $"[Paradise.Export] ktx (KTX-Software v5) not found — '{resPath}' keeps its PNG/JPEG " +
                        "textures. It renders in the editor, but the .NET runtime needs KTX2; set " +
                        "PARADISE_KTX_PATH or install KTX-Software, then re-run Paradise/Convert data GLBs → KTX2.");
                    return false;
                default:
                    GD.PushError($"[Paradise.Export] KTX2 conversion failed for '{resPath}'.");
                    return false;
            }
        }

        private static IEnumerable<string> FindDataGlbs(string dirResPath)
        {
            using var dir = DirAccess.Open(dirResPath);
            if (dir is null)
            {
                yield break;
            }

            dir.ListDirBegin();
            for (string entry = dir.GetNext(); !string.IsNullOrEmpty(entry); entry = dir.GetNext())
            {
                if (entry is "." or "..")
                {
                    continue;
                }

                string childResPath = $"{dirResPath}/{entry}";
                if (dir.CurrentIsDir())
                {
                    foreach (string nested in FindDataGlbs(childResPath))
                    {
                        yield return nested;
                    }
                }
                else if (entry.EndsWith(".glb", System.StringComparison.OrdinalIgnoreCase) ||
                         entry.EndsWith(".gltf", System.StringComparison.OrdinalIgnoreCase))
                {
                    yield return childResPath;
                }
            }

            dir.ListDirEnd();
        }
    }
}
#endif
