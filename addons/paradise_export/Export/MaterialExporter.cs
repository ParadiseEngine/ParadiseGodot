#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using ParadiseExport.Data;
using ParadiseExport.Paths;
using ParadiseExport.Serialization;

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
        private readonly Dictionary<string, string> _fieldSource = new(StringComparer.OrdinalIgnoreCase);

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

            string source = SourceId(pbr);
            string field = ExportPaths.MaterialFileField(source);
            if (_exported.ContainsKey(field))
            {
                // Same field from a different source = filename collision across directories;
                // the first registration wins, so surface it instead of silently dropping one.
                if (_fieldSource.TryGetValue(field, out string? existing) && existing != source)
                {
                    GD.PushWarning(
                        $"[ParadiseExport] Material name collision: '{source}' and '{existing}' both map to '{field}'; keeping the first.");
                }
            }
            else
            {
                _exported[field] = ToLevelMaterial(field, pbr);
                _fieldSource[field] = source;
            }

            return field;
        }

        private static LevelMaterialData ToLevelMaterial(string field, BaseMaterial3D m)
        {
            Color albedo = m.AlbedoColor.SrgbToLinear();
            // Linearize the authored colour first, THEN apply the HDR energy multiplier.
            // SrgbToLinear's gamma curve is only defined on [0,1]; multiplying before it would
            // push channels out of range and diverge from Unity's pipeline.
            Color emission = m.EmissionEnabled
                ? m.Emission.SrgbToLinear() * m.EmissionEnergyMultiplier
                : new Color(0f, 0f, 0f, 1f);

            return new LevelMaterialData
            {
                Path = field,
                Name = MaterialName(m),
                BaseColorFactor = Color32.FromRgba(albedo.R, albedo.G, albedo.B, m.AlbedoColor.A),
                BaseColorTexture = TexturePath(m.AlbedoTexture),
                MetallicFactor = m.Metallic,
                RoughnessFactor = m.Roughness,
                // ORM channel packing is deferred to Phase 6; reference whichever map exists.
                MetallicRoughnessTexture = TexturePath(m.MetallicTexture ?? m.RoughnessTexture),
                EmissiveFactor = Color32.FromRgba(emission.R, emission.G, emission.B, 1f),
                EmissiveTexture = m.EmissionEnabled ? TexturePath(m.EmissionTexture) : null,
                NormalScale = m.NormalEnabled ? m.NormalScale : 1f,
                NormalTexture = m.NormalEnabled ? TexturePath(m.NormalTexture) : null,
                OcclusionStrength = m.AOEnabled ? m.AOLightAffect : 1f,
                OcclusionTexture = m.AOEnabled ? TexturePath(m.AOTexture) : null,
                AlphaMode = AlphaModeName(m),
                RenderQueue = -1,
                // Godot's StandardMaterial3D has no glTF-transmission property, so translucency
                // is authored as a `transmission` resource-metadata float (0..1) — an explicit
                // signal with zero effect on Godot's own rendering. Absent → 0, so materials that
                // don't set it re-export byte-identical. The runtime shader's stylized glass path
                // (applyGlassResponse) consumes it.
                TransmissionFactor = m.HasMeta("transmission")
                    ? Mathf.Clamp((float)m.GetMeta("transmission").AsSingle(), 0f, 1f)
                    : 0f,
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
            // Sub-resource textures (procedural GradientTexture2D/NoiseTexture, embedded in a
            // scene/resource as "res://foo.tscn::id") are not standalone loadable files, so the
            // contract must not reference them — the runtime textures via GLB-embedded/external
            // KTX2, and such a material keeps its color factor only.
            if (path.Contains("::", StringComparison.Ordinal))
            {
                return null;
            }

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
