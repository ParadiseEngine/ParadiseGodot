#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using ParadiseExport.Core.Data;
using ParadiseExport.Core.Paths;
using ParadiseExport.Core.Serialization;
using ParadiseGodot.Authoring;
using SN = System.Numerics;

namespace ParadiseGodot.Export
{
    /// <summary>
    /// Resolves prefab-instance identity for exported entities and writes one
    /// <see cref="PrefabTemplateData"/> JSON per referenced prefab under <c>data/prefabs/</c>.
    ///
    /// Godot's prefab model is <c>PackedScene</c> instancing: a node instanced from a scene carries
    /// <see cref="Node.SceneFilePath"/>, and resources have stable <c>uid://</c> ids (the equivalent
    /// of Unity's asset GUID). Identity maps cleanly. Per-property <b>overrides</b> do NOT: Godot
    /// exposes no API equivalent to Unity's <c>PrefabUtility.GetPropertyModifications</c> (instance
    /// overrides live in the outer <c>.tscn</c> text), so override granularity is intentionally not
    /// exported here — see CONVENTIONS.md for the decision.
    /// </summary>
    internal sealed class PrefabExporter
    {
        private readonly MaterialExporter _materials;
        private readonly ExportPaths _paths;
        private readonly HashSet<string> _exportedPrefabKeys = new(StringComparer.Ordinal);

        public PrefabExporter(MaterialExporter materials, ExportPaths paths)
        {
            _materials = materials;
            _paths = paths;
        }

        public readonly record struct Identity(
            string? PrefabAssetPath,
            string? PrefabGuid,
            string? PrefabAssetType,
            string? NearestInstanceRoot);

        /// <summary>Resolve identity for an entity from its nearest scene-instance ancestor, and
        /// export that prefab's template (deduped) as a side effect.</summary>
        public Identity ResolveAndExport(EntityExport entity)
        {
            Node? instanceRoot = NearestInstanceRoot(entity);
            if (instanceRoot is null || string.IsNullOrEmpty(instanceRoot.SceneFilePath))
            {
                return default;
            }

            string assetPath = instanceRoot.SceneFilePath;
            ExportTemplate(assetPath);
            return new Identity(
                PrefabAssetPath: assetPath,
                PrefabGuid: ResolveUid(assetPath),
                PrefabAssetType: System.IO.Path.GetExtension(assetPath),
                NearestInstanceRoot: instanceRoot.Name.ToString());
        }

        private void ExportTemplate(string prefabAssetPath)
        {
            if (!_exportedPrefabKeys.Add(prefabAssetPath))
            {
                return;
            }

            PackedScene? packed = ResourceLoader.Load<PackedScene>(prefabAssetPath);
            if (packed is null || packed.Instantiate() is not Node root)
            {
                GD.PushWarning($"[ParadiseExport] Could not load prefab '{prefabAssetPath}' for template export.");
                return;
            }

            try
            {
                var template = new PrefabTemplateData
                {
                    DisplayName = root.Name.ToString(),
                    Prefab = ModelPathOf(root),
                    PrefabAssetPath = prefabAssetPath,
                    PrefabGuid = ResolveUid(prefabAssetPath),
                    PrefabAssetType = System.IO.Path.GetExtension(prefabAssetPath),
                    Materials = _materials.ExportMaterialSlots(root),
                    Entities = ShallowEntities(root),
                };

                string field = ExportPaths.PrefabFileField(prefabAssetPath);
                ExportJsonWriter.WriteJsonDocument(_paths.GetPrefabDataOutputPath(field), template);
                GD.Print($"[ParadiseExport] Exported prefab template: {field}");
            }
            finally
            {
                root.Free();
            }
        }

        // Prefab template entities are shallow (id/kind/transform/renderable): a full nested
        // component export inside templates is deferred — scene placements already carry the
        // authoritative component data via SceneDataExporter.
        private static List<LevelEntityData> ShallowEntities(Node root)
        {
            var entities = new List<LevelEntityData>();
            // Descendants() intentionally excludes `root` itself: the prefab root is the container
            // (an EntityExport produced by ModelPrefabGenerator), not a nested template entity.
            foreach (Node node in Descendants(root))
            {
                if (node is not EntityExport entity)
                {
                    continue;
                }

                SN.Vector3 localPos = new SN.Vector3(entity.Position.X, entity.Position.Y, entity.Position.Z);
                SN.Quaternion localRot = new SN.Quaternion(entity.Quaternion.X, entity.Quaternion.Y, entity.Quaternion.Z, entity.Quaternion.W);
                string name = entity.Name.ToString();
                entities.Add(new LevelEntityData
                {
                    // Template entities carry no scene-instance identity; the GUID is assigned per
                    // placement by SceneDataExporter (emitting one here would duplicate it).
                    Id = name,
                    EntityGuid = Guid.Empty,
                    StableId = name,
                    Kind = entity.ResolvedKind,
                    SpawnPhase = "LevelStart",
                    Prefab = string.IsNullOrEmpty(entity.ModelPath) ? null : entity.ModelPath,
                    LocalPosition = localPos,
                    LocalRotation = localRot,
                    LocalScale = new SN.Vector3(entity.Scale.X, entity.Scale.Y, entity.Scale.Z),
                    Components = new EntityComponentsData
                    {
                        Renderable = string.IsNullOrEmpty(entity.ModelPath) ? null : new RenderableComponentData(),
                    },
                });
            }

            return entities;
        }

        private static string? ModelPathOf(Node root) =>
            root is EntityExport entity && !string.IsNullOrEmpty(entity.ModelPath) ? entity.ModelPath : null;

        private static Node? NearestInstanceRoot(Node node)
        {
            for (Node? current = node; current is not null; current = current.GetParent())
            {
                if (!string.IsNullOrEmpty(current.SceneFilePath))
                {
                    return current;
                }
            }

            return null;
        }

        private static string? ResolveUid(string path)
        {
            long id = ResourceLoader.GetResourceUid(path);
            return id == ResourceUid.InvalidId ? null : ResourceUid.IdToText(id);
        }

        private static IEnumerable<Node> Descendants(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                yield return child;
                foreach (Node descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}
#endif
