using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParadiseCultivation;

/// <summary>
/// Player-editable LLM connection settings (endpoint, key, model override) — the values the
/// in-game panel edits. Persisted as plaintext JSON NEXT TO THE SAVES (never in the shipped
/// <c>data/</c> config: credentials don't belong in files that travel with the game). The
/// settings file, when present, wins WHOLE over the environment — a deliberately cleared key
/// stays cleared; without a file, <c>OPENAI_API_KEY</c>/<c>OPENAI_BASE_URL</c> apply as
/// before. Empty <see cref="BaseUrl"/>/<see cref="Model"/> fall back to the client default /
/// the authored <see cref="LlmConfig.Model"/>.
/// </summary>
public sealed record LlmSettings
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;

    public const string FileName = "llm-settings.json";

    public static string PathFor(string saveRoot) => Path.Combine(saveRoot, FileName);

    /// <summary>The saved file verbatim when it exists (corrupt files degrade to the
    /// environment), otherwise the environment variables.</summary>
    public static LlmSettings Resolve(string saveRoot)
    {
        try
        {
            var path = PathFor(saveRoot);
            if (File.Exists(path) &&
                JsonSerializer.Deserialize(File.ReadAllText(path), LlmSettingsJsonContext.Default.LlmSettings)
                    is { } saved)
            {
                return saved;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable settings degrade to the environment — never block startup.
        }
        return FromEnvironment();
    }

    public static LlmSettings FromEnvironment() => new()
    {
        BaseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? string.Empty,
        ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty,
        Model = string.Empty,
    };

    /// <summary>Best-effort persist (the panel shows connection state, not disk state).</summary>
    public void Save(string saveRoot)
    {
        try
        {
            Directory.CreateDirectory(saveRoot);
            File.WriteAllText(PathFor(saveRoot),
                JsonSerializer.Serialize(this, LlmSettingsJsonContext.Default.LlmSettings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A read-only save dir loses persistence, not the live connection.
        }
    }
}

/// <summary>Source-generated STJ context (AOT + collectible-ALC safe, like every other
/// serializer in the slice).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(LlmSettings))]
public sealed partial class LlmSettingsJsonContext : JsonSerializerContext;
