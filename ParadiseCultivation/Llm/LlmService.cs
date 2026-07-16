namespace ParadiseCultivation;

/// <summary>Transport seam for the OPTIONAL online intelligence layer (LLM-flavored NPC
/// replies and event narration). Implementations must be thread-safe: the runner calls this
/// from a background task — never the sim thread — and the result re-enters the simulation
/// through the command queue, where the rules layer sanitizes it before it may touch a side
/// store. Returning null (no key, network failure, refusal, timeout) means "no proposal":
/// the deterministic template text that already displayed simply stays. The core loop must
/// never require network access — this whole layer is additive flavor on top of
/// <see cref="TemplateProposer"/>, and it never writes authoritative state (affection, RNG,
/// components stay rules-derived, so determinism and the snapshot/save tests hold).</summary>
public interface ILlmTextService
{
    Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken);
}
