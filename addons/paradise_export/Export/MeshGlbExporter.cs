#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Godot;
using ParadiseExport.Paths;
using ParadiseExport.Pipeline;
using ParadiseGodot.Authoring;

namespace ParadiseGodot.Export
{
    /// <summary>
    /// Exports each entity's visual subtree to <c>data/meshes/&lt;key&gt;.glb</c> via Godot's
    /// native <see cref="GltfDocument"/> (no Blender round-trip for scene-authored meshes), in
    /// ENTITY-LOCAL space (the exported clone's root transform is identity; the entity's
    /// WorldMatrix places the instance at runtime). Deduplicates by content key — mesh resource
    /// identity + local placement — so identical crates share one GLB.
    ///
    /// Contract guarantees:
    /// - GLB primitive order == the entity's Materials slot order (both are the same
    ///   depth-first MeshInstance3D walk; the exporter feeds GltfDocument the whole subtree,
    ///   which preserves node order and per-mesh surface order).
    /// - Textures are ALWAYS KTX2: the toktx pass is MANDATORY when the GLB embeds convertible
    ///   images — ToolMissing/Failed on a textured mesh is an export error, because the engine
    ///   reader rejects PNG/JPEG payloads (KTX2-only pipeline).
    /// </summary>
    internal sealed class MeshGlbExporter
    {
        private readonly ExportPaths _paths;
        private readonly Dictionary<string, string> _fieldByKey = new();

        public MeshGlbExporter(ExportPaths paths)
        {
            _paths = paths;
        }

        /// <summary>Export <paramref name="entity"/>'s visual subtree; returns the contract
        /// field (<c>meshes/&lt;key&gt;.glb</c>) or null when the entity has no meshes.</summary>
        public string? Export(EntityExport entity)
        {
            var meshes = new List<MeshInstance3D>();
            CollectMeshInstances(entity, meshes);
            if (meshes.Count == 0)
            {
                return null;
            }

            string key = BuildContentKey(entity, meshes);
            if (_fieldByKey.TryGetValue(key, out string? cached))
            {
                return cached;
            }

            string field = ExportPaths.MeshFileField(key);
            string outputPath = _paths.GetMeshOutputPath(field);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // Write-then-move: the KTX2 pass rewrites the GLB and THROWS on missing/failed
            // toktx — running it against the final path would leave a PNG-embedded GLB behind
            // (unreadable by the engine's KTX2-only reader) and poison data/ for the runtime.
            string tempPath = outputPath + ".tmp";
            try
            {
                WriteGlb(entity, tempPath);
                RunKtx2Pass(tempPath);
                File.Move(tempPath, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }

            _fieldByKey[key] = field;
            return field;
        }

        private static void WriteGlb(EntityExport entity, string outputPath)
        {
            // Duplicate the subtree and zero the root transform so the GLB is entity-local.
            // Non-visual children (collider bodies etc.) ride along as empty glTF nodes; the
            // engine reader ignores them but keeps their transforms in the hierarchy bake.
            var clone = (Node3D)entity.Duplicate();
            try
            {
                clone.Transform = Transform3D.Identity;
                // GltfDocument.AppendFromScene exports only OWNED descendants (the same rule as
                // PackedScene.Pack); Duplicate() leaves owners pointing at the original scene
                // root, so re-own the whole cloned subtree or the GLB comes out empty.
                SetOwnerRecursive(clone, clone);
                var document = new GltfDocument();
                var state = new GltfState();
                Error error = document.AppendFromScene(clone, state);
                if (error != Error.Ok)
                {
                    throw new IOException($"GltfDocument.AppendFromScene failed for '{entity.Name}': {error}.");
                }

                byte[] bytes = document.GenerateBuffer(state);
                if (bytes is null || bytes.Length == 0)
                {
                    throw new IOException($"GltfDocument.GenerateBuffer produced no data for '{entity.Name}'.");
                }

                File.WriteAllBytes(outputPath, bytes);
            }
            finally
            {
                clone.Free();
            }
        }

        private static void SetOwnerRecursive(Node node, Node owner)
        {
            foreach (Node child in node.GetChildren())
            {
                child.Owner = owner;
                SetOwnerRecursive(child, owner);
            }
        }

        private static void RunKtx2Pass(string glbFullPath)
        {
            ToktxKtx2.ConversionResult result = ToktxKtx2.ConvertEmbeddedTextures(
                glbFullPath,
                log: message => GD.Print($"[ParadiseExport] {message}"),
                error: message => GD.PushError($"[ParadiseExport] {message}"));

            switch (result)
            {
                case ToktxKtx2.ConversionResult.NoConvertibleTextures:
                case ToktxKtx2.ConversionResult.ConvertedAllTextures:
                    return;
                case ToktxKtx2.ConversionResult.ToolMissing:
                    // KTX2-only contract: a textured GLB without the toktx pass is unreadable
                    // by the engine (PNG/JPEG images are rejected), so this is an export error,
                    // not a warning.
                    throw new IOException(
                        $"toktx is required to convert '{glbFullPath}' textures to KTX2 but was not found. " +
                        "Set PARADISE_TOKTX_PATH or install KTX-Software.");
                default:
                    throw new IOException($"KTX2 conversion failed for '{glbFullPath}'.");
            }
        }

        /// <summary>Depth-first MeshInstance3D walk — MUST match MaterialExporter's traversal
        /// so GLB primitive order equals the Materials slot order.</summary>
        private static void CollectMeshInstances(Node node, List<MeshInstance3D> meshes)
        {
            if (node is MeshInstance3D mesh)
            {
                meshes.Add(mesh);
            }

            foreach (Node child in node.GetChildren())
            {
                CollectMeshInstances(child, meshes);
            }
        }

        /// <summary>Content key over mesh resource identity + entity-local placement. Entities
        /// sharing the same visual composition (e.g. two identical crates) hash equal and share
        /// one GLB file.</summary>
        private static string BuildContentKey(EntityExport entity, List<MeshInstance3D> meshes)
        {
            var builder = new StringBuilder();
            foreach (MeshInstance3D mesh in meshes)
            {
                Transform3D local = entity.GlobalTransform.AffineInverse() * mesh.GlobalTransform;
                string meshId = mesh.Mesh is { } resource
                    ? (string.IsNullOrEmpty(resource.ResourcePath) ? resource.GetInstanceId().ToString() : resource.ResourcePath)
                    : "<null>";
                builder.Append(meshId).Append('|');
                AppendTransform(builder, local);
                builder.Append(mesh.Mesh?.GetSurfaceCount() ?? 0).Append(';');
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
            return Convert.ToHexStringLower(hash)[..16];
        }

        private static void AppendTransform(StringBuilder builder, Transform3D transform)
        {
            for (int column = 0; column < 4; column++)
            {
                Vector3 value = column == 3 ? transform.Origin : transform.Basis[column];
                builder.Append(value.X.ToString("R")).Append(',')
                       .Append(value.Y.ToString("R")).Append(',')
                       .Append(value.Z.ToString("R")).Append(',');
            }
        }
    }
}
#endif
