#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using Paradise.Export.Data;
using Paradise.Export.Serialization;

namespace ParadiseGodot
{
    /// <summary>"Paradise/Validate Export": lints the ACTIVE scene's exported contract against
    /// the failure modes that are silent at export time but break the runtime — a missing mesh
    /// reference renders nothing, a missing KTX2 sidecar fails texture load, a stale export or
    /// navmesh follows old geometry, a nudged scene root offsets every WorldMatrix. Errors are
    /// things the runtime cannot survive; warnings are drift that usually means "re-save".</summary>
    public static class ExportValidator
    {
        public static void ValidateActiveScene()
        {
            Node? root = EditorInterface.Singleton.GetEditedSceneRoot();
            if (root is null)
            {
                GD.PushWarning("[Paradise.Validate] No edited scene to validate.");
                return;
            }

            var errors = new List<string>();
            var warnings = new List<string>();
            Run(root, errors, warnings);

            foreach (string e in errors) GD.PushError($"[Paradise.Validate] {e}");
            foreach (string w in warnings) GD.PushWarning($"[Paradise.Validate] {w}");
            GD.Print($"[Paradise.Validate] {(errors.Count == 0 && warnings.Count == 0 ? "OK — no issues." : $"{errors.Count} error(s), {warnings.Count} warning(s).")}");

            var dialog = new AcceptDialog
            {
                Title = "Paradise export validation",
                DialogText = BuildReport(errors, warnings),
            };
            EditorInterface.Singleton.GetBaseControl().AddChild(dialog);
            dialog.PopupCentered();
            dialog.Confirmed += dialog.QueueFree;
            dialog.Canceled += dialog.QueueFree;
        }

        private static void Run(Node root, List<string> errors, List<string> warnings)
        {
            if (root is Node3D root3d && !root3d.Transform.IsEqualApprox(Transform3D.Identity))
            {
                errors.Add(
                    "Scene root transform is not identity — every exported WorldMatrix is offset by it. " +
                    "Reset the root transform and re-save.");
            }

            string sceneName = Export.SceneDataExporter.ResolveSceneName(root);
            var paths = ParadisePaths.ExportPaths();
            string jsonPath = paths.GetLevelDataOutputPath(sceneName);
            if (!File.Exists(jsonPath))
            {
                errors.Add($"No export at '{jsonPath}' — save the scene (auto-export) or run Paradise/Export Active Scene.");
                return;
            }

            // Staleness: the export regenerates on save, so an older-than-scene export means
            // unsaved (or externally edited) drift.
            string scenePath = ProjectSettings.GlobalizePath(root.SceneFilePath);
            if (File.Exists(scenePath) && File.GetLastWriteTimeUtc(jsonPath) < File.GetLastWriteTimeUtc(scenePath))
            {
                warnings.Add($"Export '{Path.GetFileName(jsonPath)}' is older than the scene file — re-save to refresh.");
            }

            LevelData level;
            try
            {
                level = ExportJsonReader.ReadLevel(File.ReadAllText(jsonPath));
            }
            catch (Exception ex)
            {
                errors.Add($"'{jsonPath}' does not parse as LevelData: {ex.Message}");
                return;
            }

            var checkedMeshes = new HashSet<string>(StringComparer.Ordinal);
            foreach (LevelEntityData entity in level.Entities)
            {
                string? mesh = entity.Get<RenderableComponentData>()?.Mesh;
                if (string.IsNullOrEmpty(mesh) || !checkedMeshes.Add(mesh))
                {
                    continue;
                }

                string meshPath = paths.GetMeshOutputPath(mesh);
                if (!File.Exists(meshPath))
                {
                    errors.Add($"Entity '{entity.DisplayName ?? entity.Id}' references missing mesh '{mesh}' (expected at {meshPath}).");
                    continue;
                }
                ValidateGlbImageSidecars(meshPath, mesh, errors);
            }

            string navmeshPath = paths.GetNavMeshOutputPath(sceneName);
            if (!string.IsNullOrEmpty(level.NavMeshFile) && !File.Exists(navmeshPath))
            {
                errors.Add($"Scene references navmesh '{level.NavMeshFile}' but '{navmeshPath}' does not exist.");
            }
            else if (File.Exists(navmeshPath) && File.Exists(scenePath) &&
                     File.GetLastWriteTimeUtc(navmeshPath) < File.GetLastWriteTimeUtc(scenePath))
            {
                warnings.Add("Navmesh .bin is older than the scene — paths may follow stale geometry. Re-save to re-bake.");
            }
        }

        /// <summary>Minimal GLB JSON-chunk scan: every relative <c>images[].uri</c> (the external
        /// KTX2 sidecar convention) must exist next to the GLB — the runtime resolves them
        /// relative to the mesh and cannot render the material without them.</summary>
        private static void ValidateGlbImageSidecars(string meshPath, string meshField, List<string> errors)
        {
            try
            {
                using FileStream stream = File.OpenRead(meshPath);
                using var reader = new BinaryReader(stream);
                if (reader.ReadUInt32() != 0x46546C67) // "glTF"
                {
                    return; // not a GLB container; nothing to scan
                }
                reader.ReadUInt32(); // version
                reader.ReadUInt32(); // length
                uint chunkLength = reader.ReadUInt32();
                if (reader.ReadUInt32() != 0x4E4F534A) // "JSON"
                {
                    return;
                }

                using JsonDocument gltf = JsonDocument.Parse(reader.ReadBytes(checked((int)chunkLength)));
                if (!gltf.RootElement.TryGetProperty("images", out JsonElement images))
                {
                    return;
                }

                string meshDir = Path.GetDirectoryName(meshPath) ?? ".";
                foreach (JsonElement image in images.EnumerateArray())
                {
                    if (!image.TryGetProperty("uri", out JsonElement uriElement))
                    {
                        continue; // embedded bufferView image
                    }
                    string? uri = uriElement.GetString();
                    if (string.IsNullOrEmpty(uri) || uri.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!File.Exists(Path.Combine(meshDir, Uri.UnescapeDataString(uri))))
                    {
                        errors.Add($"Mesh '{meshField}' references image '{uri}' but the sidecar is missing next to the GLB.");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Mesh '{meshField}' could not be scanned: {ex.Message}");
            }
        }

        private static string BuildReport(List<string> errors, List<string> warnings)
        {
            if (errors.Count == 0 && warnings.Count == 0)
            {
                return "No issues found — the export is consistent with the scene.";
            }
            var text = new System.Text.StringBuilder();
            foreach (string e in errors) text.AppendLine($"ERROR: {e}");
            foreach (string w in warnings) text.AppendLine($"warning: {w}");
            return text.ToString();
        }
    }
}
#endif
