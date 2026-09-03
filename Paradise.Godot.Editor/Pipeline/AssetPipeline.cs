#if TOOLS
using System.Collections.Generic;
using System.IO;
using Godot;
using Paradise.Assets.Pipeline;

namespace ParadiseGodot.Pipeline
{
    /// <summary>
    /// Godot entry point for the asset pipeline: walks <c>res://models</c> for FBX files and runs the
    /// engine-neutral Core converters — Blender FBX→GLB, then `ktx create` GLB→KTX2. Both degrade gracefully
    /// when the external CLI is missing (Blender / ktx). Triggered from the
    /// <c>Paradise/Convert Models (FBX→GLB→KTX2)</c> menu.
    /// </summary>
    internal static class AssetPipeline
    {
        private const string ModelsDir = "res://models";

        public static void ConvertAllModels()
        {
            string repoRoot = ProjectSettings.GlobalizePath("res://");
            int fbxCount = 0, glbCount = 0, ktx2Count = 0;

            foreach (string fbxRes in FindFiles(ModelsDir, ".fbx"))
            {
                fbxCount++;
                string fbxFull = ProjectSettings.GlobalizePath(fbxRes);
                string glbFull = ProjectSettings.GlobalizePath(GlbPathFor(fbxRes));

                BlenderFbxGlb.Result blender = BlenderFbxGlb.Convert(
                    fbxFull, glbFull, force: false,
                    log: msg => GD.Print($"[Paradise.Assets] {msg}"),
                    error: msg => GD.PushError($"[Paradise.Assets] {msg}"));

                if (blender is not (BlenderFbxGlb.Result.Converted or BlenderFbxGlb.Result.UpToDate))
                {
                    continue;
                }

                glbCount++;
                // The externalTextureRoot argument is gone: 0.34's split has the rewriter place
                // sidecars beside the GLB itself, which is where this always pointed it anyway.
                ConversionResult ktx2 = GlbTextureWorkflows.ConvertEmbeddedTextures(
                    glbFull,
                    repoRoot,
                    log: msg => GD.Print($"[Paradise.Assets] {msg}"),
                    error: msg => GD.PushError($"[Paradise.Assets] {msg}"));

                if (ktx2 == ConversionResult.ConvertedAllTextures)
                {
                    ktx2Count++;
                }
            }

            GD.Print($"[Paradise.Export] Model pipeline complete: {fbxCount} FBX, {glbCount} GLB, {ktx2Count} KTX2-converted.");
            // Surface the freshly written .glb/.ktx2 files in the editor's FileSystem dock.
            EditorInterface.Singleton.GetResourceFilesystem().Scan();
        }

        // <dir>/<name>_GLB.glb, matching the Unity tool's generated-GLB naming.
        private static string GlbPathFor(string fbxResPath)
        {
            string directory = fbxResPath[..fbxResPath.LastIndexOf('/')];
            string name = Path.GetFileNameWithoutExtension(fbxResPath);
            return $"{directory}/{name}_GLB.glb";
        }

        private static IEnumerable<string> FindFiles(string dirPath, string extension)
        {
            using DirAccess? dir = DirAccess.Open(dirPath);
            if (dir is null)
            {
                yield break;
            }

            foreach (string sub in dir.GetDirectories())
            {
                foreach (string nested in FindFiles($"{dirPath}/{sub}", extension))
                {
                    yield return nested;
                }
            }

            foreach (string file in dir.GetFiles())
            {
                if (file.ToLowerInvariant().EndsWith(extension))
                {
                    yield return $"{dirPath}/{file}";
                }
            }
        }
    }
}
#endif
