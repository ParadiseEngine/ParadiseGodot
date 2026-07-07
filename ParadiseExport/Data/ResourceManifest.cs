#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ParadiseExport.Data
{
    /// <summary>
    /// Stable resource identity for the export contract (schema v3): level documents reference
    /// assets by GUID and <c>data/resources.json</c> maps each GUID to its data-relative file.
    /// GUIDs keep resource identity uniform with the contract's existing entity/prefab GUIDs.
    /// Generated artifacts (content-keyed GLBs, materials, navmesh) carry deterministic
    /// NAME-BASED GUIDs (SHA-256 of the data-relative path — which for meshes already embeds
    /// the content key — folded into an RFC 9562 v8 GUID); references to file-backed Godot
    /// source resources can later fold Godot's <c>uid://</c> through the same minting.
    /// </summary>
    public static class ResourceGuid
    {
        /// <summary>True when a reference field carries a well-formed GUID.</summary>
        public static bool IsGuid(string? value) => Guid.TryParse(value, out _);

        /// <summary>Deterministic name-based GUID: same input → same GUID across exports and
        /// machines. SHA-256 folded to 16 bytes with the RFC 9562 version-8 (custom) bits set,
        /// serialized in the dashed lowercase format STJ uses for the contract's other GUIDs.</summary>
        public static string FromString(string value)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
            Span<byte> guid = hash[..16];
            guid[7] = (byte)((guid[7] & 0x0F) | 0x80); // version 8 (custom, name-based)
            guid[8] = (byte)((guid[8] & 0x3F) | 0x80); // RFC variant
            return new Guid(guid, bigEndian: true).ToString("D");
        }
    }

    /// <summary>The GUID → data-relative-path map written to <c>data/resources.json</c>.</summary>
    public sealed record ResourceManifestData
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public Dictionary<string, string> Resources { get; set; } = new();
    }

    /// <summary>Export-time collector: exporters register every artifact they emit and put the
    /// returned GUID into the level document instead of the path.</summary>
    public sealed class ResourceManifestBuilder
    {
        private readonly ResourceManifestData _data = new();

        public ResourceManifestData Data => _data;

        /// <summary>Register a generated artifact by its data-relative path; returns its minted
        /// GUID. Idempotent — the same path always yields the same GUID and entry.</summary>
        public string Register(string dataRelativePath)
        {
            string guid = ResourceGuid.FromString(dataRelativePath);
            _data.Resources[guid] = dataRelativePath;
            return guid;
        }

        /// <summary>Register under an externally-derived GUID (e.g. one minted from a Godot
        /// ResourceUid for a file-backed source asset).</summary>
        public string Register(string guid, string dataRelativePath)
        {
            _data.Resources[guid] = dataRelativePath;
            return guid;
        }
    }
}
