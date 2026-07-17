using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParadiseCultivation;

/// <summary>
/// Minimal OpenAI Chat Completions client behind <see cref="ILlmTextService"/>. Deliberately
/// raw HttpClient + source-generated STJ instead of an SDK: this assembly publishes NativeAOT
/// (ParadiseRuntime) and loads into Godot's collectible AssemblyLoadContext, so it must stay
/// free of reflection-based serialization and heavyweight dependencies (the same reasoning
/// as <see cref="CultivationJsonContext"/>).
///
/// Credentials come from <see cref="LlmSettings"/> — the in-game panel's saved file or the
/// <c>OPENAI_API_KEY</c>/<c>OPENAI_BASE_URL</c> environment (see <see cref="LlmSettings.Resolve"/>)
/// — never from config files that ship with the game. Any OpenAI-compatible server works
/// (vLLM, GLM, Qwen/DashScope, …). No credential → <see cref="TryCreate"/> returns null and
/// the game stays fully offline on the template proposer.
/// </summary>
public sealed class OpenAiLlmClient : ILlmTextService, IDisposable
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly LlmConfig _config;
    /// <summary>Current OpenAI models take <c>max_completion_tokens</c>; some compatible
    /// servers only know the legacy <c>max_tokens</c>. Start modern, fall back once on 400
    /// and remember.</summary>
    private volatile bool _useLegacyMaxTokens;

    private OpenAiLlmClient(HttpClient http, Uri endpoint, LlmConfig config, string baseUrl, string model)
    {
        _http = http;
        _endpoint = endpoint;
        _config = config;
        BaseUrl = baseUrl;
        Model = model;
    }

    /// <summary>The endpoint root in use (settings value or <see cref="DefaultBaseUrl"/>) —
    /// the in-game panel shows it.</summary>
    public string BaseUrl { get; }

    /// <summary>The model in use (settings override or the authored config default).</summary>
    public string Model { get; }

    /// <summary>Null when the feature is disabled in config, no key is present, or the URL is
    /// malformed — callers wire the returned client into <see cref="CultivationRunner.Llm"/>
    /// (which takes ownership and disposes it).</summary>
    public static OpenAiLlmClient? TryCreate(LlmConfig config, LlmSettings settings)
    {
        if (!config.Enabled || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return null;
        }

        var root = (string.IsNullOrWhiteSpace(settings.BaseUrl) ? DefaultBaseUrl : settings.BaseUrl).TrimEnd('/');
        Uri endpoint;
        try
        {
            endpoint = new Uri($"{root}/chat/completions");
        }
        catch (UriFormatException)
        {
            return null; // a typo in the panel must not throw into the UI draw
        }
        var model = string.IsNullOrWhiteSpace(settings.Model) ? config.Model : settings.Model.Trim();

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds)) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", settings.ApiKey.Trim());
        return new OpenAiLlmClient(http, endpoint, config, root, model);
    }

    public async Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        try
        {
            var response = await PostAsync(systemPrompt, userPrompt, _useLegacyMaxTokens, cancellationToken)
                .ConfigureAwait(false);
            if (response is null && !_useLegacyMaxTokens)
            {
                // One retry with the legacy token-limit field for older compatible servers.
                _useLegacyMaxTokens = true;
                response = await PostAsync(systemPrompt, userPrompt, legacyMaxTokens: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            var text = response?.Choices is { Length: > 0 } choices ? choices[0].Message?.Content : null;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or UriFormatException)
        {
            return null; // best-effort layer: any failure keeps the deterministic fallback
        }
    }

    private async Task<ChatCompletionResponse?> PostAsync(
        string systemPrompt, string userPrompt, bool legacyMaxTokens, CancellationToken cancellationToken)
    {
        var request = new ChatCompletionRequest
        {
            Model = Model,
            MaxCompletionTokens = legacyMaxTokens ? null : _config.MaxTokens,
            MaxTokens = legacyMaxTokens ? _config.MaxTokens : null,
            Messages =
            [
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user", Content = userPrompt },
            ],
        };
        using var content = new StringContent(
            JsonSerializer.Serialize(request, OpenAiJsonContext.Default.ChatCompletionRequest),
            Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(body, OpenAiJsonContext.Default.ChatCompletionResponse);
    }

    public void Dispose() => _http.Dispose();
}

// ---- wire DTOs (snake_case field names per the Chat Completions API) ------------------------

public sealed record ChatCompletionRequest
{
    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("max_completion_tokens")] public int? MaxCompletionTokens { get; init; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; init; }
    [JsonPropertyName("messages")] public required ChatMessage[] Messages { get; init; }
}

public sealed record ChatMessage
{
    [JsonPropertyName("role")] public required string Role { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
}

public sealed record ChatCompletionResponse
{
    [JsonPropertyName("choices")] public ChatChoice[]? Choices { get; init; }
}

public sealed record ChatChoice
{
    [JsonPropertyName("message")] public ChatMessage? Message { get; init; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; init; }
}

/// <summary>Source-generated STJ context for the OpenAI wire types — AOT-safe and free of
/// the reflection caches that break Godot's collectible AssemblyLoadContext. Number reading
/// from strings tolerated: compat models sometimes quote the affection number.</summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(LlmChatProposal))]
public sealed partial class OpenAiJsonContext : JsonSerializerContext;
