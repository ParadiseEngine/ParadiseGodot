namespace ParadiseCultivation.Tests;

/// <summary>The panel-editable LLM connection settings: a saved file wins whole over the
/// environment, corrupt files degrade instead of throwing, and client creation resolves the
/// endpoint/model fallbacks without ever throwing into the UI draw.</summary>
public class LlmSettingsTests
{
    private static string TempRoot() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"cultivation-llm-settings-{Guid.NewGuid():N}")).FullName;

    [Test]
    public async Task settings_round_trip_through_the_save_root()
    {
        var root = TempRoot();
        try
        {
            new LlmSettings { BaseUrl = "https://example.test/v1", ApiKey = "sk-abc", Model = "glm-5" }
                .Save(root);
            var loaded = LlmSettings.Resolve(root);

            await Assert.That(loaded.BaseUrl).IsEqualTo("https://example.test/v1");
            await Assert.That(loaded.ApiKey).IsEqualTo("sk-abc");
            await Assert.That(loaded.Model).IsEqualTo("glm-5");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task a_saved_file_wins_whole_even_with_an_empty_key()
    {
        var root = TempRoot();
        try
        {
            // The player pressed "disconnect": the cleared key must stay cleared on the next
            // launch even if the environment still carries one.
            new LlmSettings { ApiKey = string.Empty }.Save(root);
            var loaded = LlmSettings.Resolve(root);
            await Assert.That(loaded.ApiKey).IsEqualTo(string.Empty);
            await Assert.That(OpenAiLlmClient.TryCreate(Fixture.Config.Llm, loaded)).IsNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task a_corrupt_settings_file_degrades_instead_of_throwing()
    {
        var root = TempRoot();
        try
        {
            File.WriteAllText(LlmSettings.PathFor(root), "{not json");
            var loaded = LlmSettings.Resolve(root); // must not throw
            await Assert.That(loaded).IsNotNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task client_creation_resolves_endpoint_and_model_fallbacks()
    {
        // No key → no client (offline).
        await Assert.That(OpenAiLlmClient.TryCreate(Fixture.Config.Llm, new LlmSettings())).IsNull();

        // Key only → default endpoint, authored default model.
        using var defaults = OpenAiLlmClient.TryCreate(
            Fixture.Config.Llm, new LlmSettings { ApiKey = "sk-abc" });
        await Assert.That(defaults).IsNotNull();
        await Assert.That(defaults!.BaseUrl).IsEqualTo(OpenAiLlmClient.DefaultBaseUrl);
        await Assert.That(defaults.Model).IsEqualTo(Fixture.Config.Llm.Model);

        // Overrides (with a trailing slash to normalize) → taken verbatim.
        using var custom = OpenAiLlmClient.TryCreate(Fixture.Config.Llm, new LlmSettings
        {
            ApiKey = "sk-abc",
            BaseUrl = "https://example.test/v1/",
            Model = "kimi-k2.5",
        });
        await Assert.That(custom!.BaseUrl).IsEqualTo("https://example.test/v1");
        await Assert.That(custom.Model).IsEqualTo("kimi-k2.5");

        // A garbage URL yields null, never an exception into the UI draw.
        var garbage = OpenAiLlmClient.TryCreate(Fixture.Config.Llm, new LlmSettings
        {
            ApiKey = "sk-abc",
            BaseUrl = "not a url at all ::",
        });
        await Assert.That(garbage).IsNull();
    }

    [Test]
    public async Task replacing_the_runner_client_disposes_the_old_one_and_swaps()
    {
        using var runner = Fixture.NewRunner();
        var first = OpenAiLlmClient.TryCreate(Fixture.Config.Llm, new LlmSettings { ApiKey = "sk-a" })!;
        runner.ReplaceLlm(first);
        await Assert.That(runner.Llm).IsSameReferenceAs(first);

        var second = OpenAiLlmClient.TryCreate(Fixture.Config.Llm, new LlmSettings { ApiKey = "sk-b" })!;
        runner.ReplaceLlm(second);
        await Assert.That(runner.Llm).IsSameReferenceAs(second);

        // Disconnect: back to the fully offline template proposer.
        runner.ReplaceLlm(null);
        await Assert.That(runner.Llm).IsNull();
    }
}
