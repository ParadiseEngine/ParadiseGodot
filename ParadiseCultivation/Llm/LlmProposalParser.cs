using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParadiseCultivation;

/// <summary>The LLM chat reply's structured form: the prompt asks for one JSON line with the
/// in-character reply plus an affection-delta suggestion (the full
/// <see cref="InteractionProposal"/> shape — "LLMs propose, rules decide": the suggestion is
/// clamped to the config budget before it may adjust anything).</summary>
public sealed record LlmChatProposal
{
    [JsonPropertyName("reply")] public string? Reply { get; init; }
    [JsonPropertyName("affection")] public float? Affection { get; init; }
}

/// <summary>Lenient extraction of <see cref="LlmChatProposal"/> from raw model output —
/// models on arbitrary OpenAI-compatible servers wrap JSON in prose or markdown fences, or
/// ignore the format request entirely. Anything unparseable degrades to "the whole text is
/// the reply, no affection suggestion" (exactly the pre-structured behavior).</summary>
public static class LlmProposalParser
{
    public static (string Reply, float? Affection) Parse(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                var proposal = JsonSerializer.Deserialize(
                    text.AsSpan(start, end - start + 1), OpenAiJsonContext.Default.LlmChatProposal);
                if (proposal is { Reply: { } reply } && !string.IsNullOrWhiteSpace(reply))
                {
                    return (reply, proposal.Affection);
                }
            }
            catch (JsonException)
            {
                // Braces without valid JSON — treat the whole text as the reply.
            }
        }
        return (text, null);
    }
}
