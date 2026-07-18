#if TOOLS
using System.Collections.Generic;
using Godot;
using ParadiseGodot.Authoring;

namespace ParadiseGodot.Pipeline
{
    /// <summary>
    /// Generates entity prefabs (<c>.tscn</c>) from GLB/glTF models — the Godot equivalent of the
    /// Unity ModelPrefabGenerator. Each prefab is a clean <see cref="EntityExport"/> root with the
    /// model instanced as a child, so authored entity settings + colliders live on the root while
    /// the model child tracks the source asset.
    ///
    /// Idempotent: an existing prefab is left untouched, preserving hand-authored roots (the
    /// equivalent of the Unity tool's GUID-preserving regenerate).
    /// </summary>
    internal static class ModelPrefabGenerator
    {
        private const string ModelsDir = "res://models";
        private const string PrefabsDir = "res://prefabs/models";

        public static int GenerateAll()
        {
            int generated = 0;
            foreach (string modelPath in FindModels(ModelsDir))
            {
                if (GenerateForModel(modelPath))
                {
                    generated++;
                }
            }

            GD.Print($"[Paradise.Export] Model prefab generation complete: {generated} written.");
            return generated;
        }

        public static bool GenerateForModel(string modelResPath)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(modelResPath);
            string prefabPath = $"{PrefabsDir}/{name}.tscn";
            if (ResourceLoader.Exists(prefabPath))
            {
                return false; // idempotent: keep the hand-authored prefab
            }

            PackedScene? modelScene = ResourceLoader.Load<PackedScene>(modelResPath);
            if (modelScene is null || modelScene.Instantiate() is not Node modelInstance)
            {
                GD.PushWarning($"[Paradise.Export] Could not load model '{modelResPath}'.");
                return false;
            }

            var root = new EntityExport { Name = name, ModelPath = modelResPath };
            modelInstance.Name = name;
            // Mark the child as an instance of the source scene so the packed prefab references it
            // (re-imports flow through) rather than embedding a frozen copy.
            modelInstance.SceneFilePath = modelResPath;
            root.AddChild(modelInstance);
            modelInstance.Owner = root;

            var packed = new PackedScene();
            try
            {
                if (packed.Pack(root) != Error.Ok)
                {
                    return false;
                }

                DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(PrefabsDir));
                Error saveResult = ResourceSaver.Save(packed, prefabPath);
                if (saveResult != Error.Ok)
                {
                    GD.PushWarning($"[Paradise.Export] Failed to save prefab '{prefabPath}': {saveResult}");
                    return false;
                }

                GD.Print($"[Paradise.Export] Generated prefab: {prefabPath}");
                return true;
            }
            finally
            {
                // Always free the in-memory tree, even if save/dir creation throws.
                root.Free();
            }
        }

        private static IEnumerable<string> FindModels(string dirPath)
        {
            using DirAccess? dir = DirAccess.Open(dirPath);
            if (dir is null)
            {
                yield break;
            }

            foreach (string sub in dir.GetDirectories())
            {
                foreach (string nested in FindModels($"{dirPath}/{sub}"))
                {
                    yield return nested;
                }
            }

            foreach (string file in dir.GetFiles())
            {
                string lower = file.ToLowerInvariant();
                if (lower.EndsWith(".glb") || lower.EndsWith(".gltf"))
                {
                    yield return $"{dirPath}/{file}";
                }
            }
        }
    }
}
#endif
