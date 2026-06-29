#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using ParadiseExport.Core.Data;
using ParadiseExport.Core.Paths;
using ParadiseExport.Core.Serialization;

namespace ParadiseGodot.Export
{
    /// <summary>
    /// Maps Godot <see cref="BaseMaterial3D"/> (StandardMaterial3D / ORMMaterial3D) to engine-neutral
    /// <see cref="LevelMaterialData"/>, writing one JSON per material under <c>data/materials/</c>.
    ///
    /// Albedo/emissive colours are converted sRGB→linear (<see cref="Color.SrgbToLinear"/>) to match
    /// the Unity tool's linear output (see CONVENTIONS.md). Textures are referenced by their
    /// project-relative source path; texture *conversion* (PNG/KTX2) is the asset pipeline's job in
    /// a later phase, not this exporter's.
    /// </summary>
    internal sealed class MaterialExporter
    {
        private readonly Dictionary<string, LevelMaterialData> _exported = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-surface material field paths for a node's MeshInstance3D descendants (the
        /// entity's <c>Materials</c> slot list), registering each unique material for writing.</summary>
        public List<string?> ExportMaterialSlots(Node node)
        {
            var slots = new List<string?>();
            foreach (MeshInstance3D mesh in MeshInstances(node))
            {
                int surfaceCount = mesh.Mesh?.GetSurfaceCount() ?? 0;
                for (int surface = 0; surface < surfaceCount; surface++)
                {
                    slots.Add(Register(mesh.GetActiveMaterial(surface)));
                }
            }

            return slots;
        }

        public void WriteExportedMaterials(ExportPaths paths)
        {
            foreach (LevelMaterialData material in _exported.Values)
            {
                ExportJsonWriter.WriteJsonDocument(paths.GetMaterialDataOutputPath(material.Path), material);
            }
        }

        private string? Register(Material? material)
        {
            if (material is not BaseMaterial3D pbr)
            {
                return null;
            }

            string field = ExportPaths.MaterialFileField(SourceId(pbr));
            if (!_exported.ContainsKey(field))
            {
                _exported[field] = ToLevelMaterial(field, pbr);
            }

            return field;
        }

        private static LevelMaterialData ToLevelMaterial(string field, BaseMaterial3D m)
        {
            Color albedo = m.AlbedoColor.SrgbToLinear();
            Color emission = m.EmissionEnabled
                ? (m.Emission * m.EmissionEnergyMultiplier).SrgbToLinear()
                : new Color(0f, 0f, 0f, 1f);

            return new LevelMaterialData
            {
                Path = field,
                Name = MaterialName(m),
                BaseColorFactor = Color32.FromRgba(albedo.R, albedo.G, albedo.B, m.AlbedoColor.A),
                BaseColorTexture = TexturePath(m.AlbedoTexture),
                MetallicFactor = m.Metallic,
                RoughnessFactor = m.Roughness,
                MetallicRoughnessTexture = TexturePath(m.MetallicTexture ?? m.RoughnessTexture),
                EmissiveFactor = Color32.FromRgba(emission.R, emission.G, emission.B, 1f),
                EmissiveTexture = m.EmissionEnabled ? TexturePath(m.EmissionTexture) : null,
                NormalScale = m.NormalEnabled ? m.NormalScale : 1f,
                NormalTexture = m.NormalEnabled ? TexturePath(m.NormalTexture) : null,
                OcclusionStrength = 1f,
                OcclusionTexture = m.AOEnabled ? TexturePath(m.AOTexture) : null,
                AlphaMode = AlphaModeName(m),
                RenderQueue = -1,
                TransmissionFactor = 0f,
            };
        }

        private static string AlphaModeName(BaseMaterial3D m)
        {
            if (m.AlbedoColor.A < 0.999f)
            {
                return "Blend";
            }

            return m.Transparency switch
            {
                BaseMaterial3D.TransparencyEnum.Disabled => "Opaque",
                BaseMaterial3D.TransparencyEnum.AlphaScissor => "Mask",
                BaseMaterial3D.TransparencyEnum.AlphaHash => "Mask",
                _ => "Blend",
            };
        }

        private static string SourceId(BaseMaterial3D m) =>
            !string.IsNullOrEmpty(m.ResourcePath) ? m.ResourcePath
            : !string.IsNullOrEmpty(m.ResourceName) ? m.ResourceName
            : "material";

        private static string MaterialName(BaseMaterial3D m) =>
            !string.IsNullOrEmpty(m.ResourceName) ? m.ResourceName
            : !string.IsNullOrEmpty(m.ResourcePath) ? System.IO.Path.GetFileNameWithoutExtension(m.ResourcePath)
            : "Material";

        private static string? TexturePath(Texture2D? texture)
        {
            if (texture is null || string.IsNullOrEmpty(texture.ResourcePath))
            {
                return null;
            }

            string path = texture.ResourcePath;
            return path.StartsWith("res://", StringComparison.Ordinal) ? path["res://".Length..] : path;
        }

        private static IEnumerable<MeshInstance3D> MeshInstances(Node node)
        {
            if (node is MeshInstance3D mesh)
            {
                yield return mesh;
            }

            foreach (Node child in node.GetChildren())
            {
                foreach (MeshInstance3D descendant in MeshInstances(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}
#endif
