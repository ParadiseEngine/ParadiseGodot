#if TOOLS
using System.Collections.Generic;
using System.IO;
using Godot;
using ParadiseExport.Pipeline;

namespace ParadiseGodot.Pipeline
{
    /// <summary>
    /// Converts the embedded textures of GLBs living under <c>res://data/</c> to KTX2 (Basis
    /// Universal) IN PLACE via <see cref="KtxCreate"/>. This is the ingest step of the
    /// source-GLB pipeline: the .NET runtime's GLB reader is KTX2-only, so any GLB it will load
    /// must have its PNG/JPEG textures transcoded once at import time (not baked per scene export).
    ///
    /// Idempotent: an already-KTX2 GLB yields <see cref="KtxCreate.ConversionResult.NoConvertibleTextures"/>
    /// and is left untouched, so re-running (and the import hook's reimport) never loops.
    /// A missing <c>ktx</c> CLI is a WARNING here (not fatal): the GLB stays PNG and still renders
    /// in the Godot editor; only the .NET runtime needs the KTX2 form.
    /// </summary>
    internal static class DataGlbConverter
    {
        private const string DataDir = "res://data";

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

            GD.Print($"[ParadiseExport] data/ GLB KTX2 pass: {converted} converted.");
            return converted;
        }

        /// <summary>Convert a single GLB in place. <paramref name="rewritten"/> is true only when
        /// the file was actually changed (textures transcoded) — the caller uses it to decide
        /// whether a reimport is needed. Returns false only on hard failure.</summary>
        public static bool Convert(string resPath, out bool rewritten)
        {
            rewritten = false;
            string full = ProjectSettings.GlobalizePath(resPath);
            KtxCreate.ConversionResult result = KtxCreate.ExternalizeTextures(
                full,
                repoRoot: ProjectSettings.GlobalizePath("res://"),
                log: msg => GD.Print($"[ParadiseExport] {msg}"),
                error: msg => GD.PushError($"[ParadiseExport] {msg}"));

            switch (result)
            {
                case KtxCreate.ConversionResult.ConvertedAllTextures:
                    rewritten = true;
                    return true;
                case KtxCreate.ConversionResult.NoConvertibleTextures:
                    return true; // already KTX2 / untextured — nothing to do (idempotent)
                case KtxCreate.ConversionResult.ToolMissing:
                    GD.PushWarning(
                        $"[ParadiseExport] ktx (KTX-Software v5) not found — '{resPath}' keeps its PNG/JPEG " +
                        "textures. It renders in the editor, but the .NET runtime needs KTX2; set " +
                        "PARADISE_KTX_PATH or install KTX-Software, then re-run Paradise/Convert data GLBs → KTX2.");
                    return false;
                default:
                    GD.PushError($"[ParadiseExport] KTX2 conversion failed for '{resPath}'.");
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
