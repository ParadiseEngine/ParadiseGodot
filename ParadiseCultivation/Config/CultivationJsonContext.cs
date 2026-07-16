using System.Text.Json.Serialization;

namespace ParadiseCultivation;

/// <summary>Source-generated STJ context — AOT-safe (ParadiseRuntime publishes NativeAOT) and
/// free of the reflection caches that break Godot's collectible AssemblyLoadContext.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(CultivationConfig))]
[JsonSerializable(typeof(SaveData))]
public sealed partial class CultivationJsonContext : JsonSerializerContext;
