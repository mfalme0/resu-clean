using ResuClean.Services.Providers;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// Provider presets and model discovery. Every base URL here was checked against the provider's own
/// documentation; the parsing tests use the real response shapes.
/// </summary>
public class ProviderPresetTests
{
    [Theory]
    [InlineData("openai", "https://api.openai.com/v1", "openai")]
    [InlineData("anthropic", "https://api.anthropic.com", "anthropic")]
    [InlineData("openrouter", "https://openrouter.ai/api/v1", "openai")]
    [InlineData("groq", "https://api.groq.com/openai/v1", "openai")]
    [InlineData("gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "openai")]
    [InlineData("nvidia", "https://integrate.api.nvidia.com/v1", "openai")]
    [InlineData("cleanapis", "https://cleanapis.com/v1", "openai")]
    [InlineData("ollama", "http://127.0.0.1:11434/v1", "openai")]
    [InlineData("lmstudio", "http://127.0.0.1:1234/v1", "openai")]
    public void Presets_use_the_documented_base_urls(string id, string expectedUrl, string expectedType)
    {
        var preset = ProviderPresets.Find(id);
        Assert.NotNull(preset);
        Assert.Equal(expectedUrl, preset!.BaseUrl);
        Assert.Equal(expectedType, preset.Type);
    }

    [Fact]
    public void Every_preset_suggests_at_least_one_model()
    {
        Assert.NotEmpty(ProviderPresets.All);
        foreach (var preset in ProviderPresets.All)
        {
            Assert.NotEmpty(preset.SuggestedModels);
            Assert.NotEmpty(preset.Notes);
            Assert.NotEmpty(preset.KeyHint);
            Assert.StartsWith("https://", preset.DocsUrl);
        }
    }

    [Fact]
    public void Only_local_presets_are_marked_keyless()
    {
        foreach (var preset in ProviderPresets.All)
        {
            var local = preset.BaseUrl.Contains("127.0.0.1", StringComparison.Ordinal);
            Assert.Equal(local, preset.Keyless);
        }
    }

    [Fact]
    public void Cloud_presets_declare_an_environment_variable()
    {
        foreach (var preset in ProviderPresets.All.Where(p => !p.Keyless))
        {
            Assert.EndsWith("_API_KEY", preset.EnvVar);
        }
    }

    [Fact]
    public void Presets_are_exposed_as_dtos_for_the_api()
    {
        var dtos = ProviderPresets.AsDtos();
        Assert.Equal(ProviderPresets.All.Length, dtos.Count);
        Assert.Contains(dtos, d => d.Id == "cleanapis" && d.BaseUrl == "https://cleanapis.com/v1");
        Assert.Contains(dtos, d => d.Id == "nvidia" && d.BaseUrl == "https://integrate.api.nvidia.com/v1");
        Assert.Contains(dtos, d => d.Id == "gemini" && d.BaseUrl.Contains("generativelanguage.googleapis.com"));
    }

    [Fact]
    public void An_unknown_preset_id_returns_nothing()
    {
        Assert.Null(ProviderPresets.Find("not-a-provider"));
    }
}

public class ModelDiscoveryTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static string Read(string name) => File.ReadAllText(Path.Combine(Fixtures, name));

    [Fact]
    public void Openai_shape_is_parsed_deduplicated_and_filtered()
    {
        var models = ProviderService.ParseModelList(Read("models-openai.json"), "https://api.openai.com/v1");

        Assert.Equal(new[] { "gpt-4o", "gpt-4o-mini" }, models);
        Assert.DoesNotContain("text-embedding-3-small", models);
    }

    [Fact]
    public void Gemini_ids_lose_the_models_prefix_and_non_chat_models_are_dropped()
    {
        var models = ProviderService.ParseModelList(
            Read("models-gemini.json"), "https://generativelanguage.googleapis.com/v1beta/openai");

        Assert.Equal(new[] { "gemini-2.0-flash", "gemini-2.5-flash", "gemini-2.5-pro" }, models);
        Assert.DoesNotContain(models, m => m.StartsWith("models/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("text-embedding-004", models);
        Assert.DoesNotContain("imagen-3.0-generate-002", models);
    }

    [Fact]
    public void The_prefix_is_only_stripped_for_gemini()
    {
        var models = ProviderService.ParseModelList(Read("models-gemini.json"), "https://example.org/v1");
        Assert.Contains("models/gemini-2.5-pro", models);
    }

    [Fact]
    public void Groq_shape_is_parsed()
    {
        var models = ProviderService.ParseModelList(Read("models-groq.json"), "https://api.groq.com/openai/v1");
        Assert.Equal(new[] { "llama-3.1-8b-instant", "llama-3.3-70b-versatile", "openai/gpt-oss-120b" }, models);
    }

    [Fact]
    public void Ollama_answers_with_name_instead_of_id()
    {
        var models = ProviderService.ParseModelList(Read("models-ollama.json"), "http://127.0.0.1:11434/v1");
        Assert.Equal(new[] { "llama3.2:latest", "qwen2.5:7b" }, models);
    }

    [Fact]
    public void Anthropic_shape_is_parsed_the_same_way()
    {
        const string body = """{"has_more":false,"data":[{"id":"claude-sonnet-4-5","type":"model"}],"first_id":"claude-sonnet-4-5","last_id":"claude-sonnet-4-5"}""";
        var models = ProviderService.ParseModelList(body, "https://api.anthropic.com");
        Assert.Equal(new[] { "claude-sonnet-4-5" }, models);
    }

    [Fact]
    public void A_bare_array_of_strings_is_accepted()
    {
        var models = ProviderService.ParseModelList("""["a","b","c"]""", "https://example.org/v1");
        Assert.Equal(new[] { "a", "b", "c" }, models);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"unexpected\": true }")]
    [InlineData("{\"data\": []}")]
    public void Unusable_responses_produce_an_empty_list_rather_than_an_error(string body)
    {
        Assert.Empty(ProviderService.ParseModelList(body, "https://example.org/v1"));
    }

    [Theory]
    [InlineData("text-embedding-3-large", false)]
    [InlineData("nomic-embed-text", false)]
    [InlineData("whisper-large-v3", false)]
    [InlineData("imagen-3.0-generate-002", false)]
    [InlineData("omni-moderation-latest", false)]
    [InlineData("bge-reranker-v2-m3", false)]
    [InlineData("gpt-4o-mini", true)]
    [InlineData("claude-sonnet-4-5", true)]
    [InlineData("gemini-2.5-flash", true)]
    [InlineData("llama-3.3-70b-versatile", true)]
    public void Only_chat_models_can_be_routed(string modelId, bool expected)
    {
        Assert.Equal(expected, ProviderService.IsChatCapable(modelId));
    }

    [Fact]
    public async Task Discovery_without_a_key_explains_itself_instead_of_throwing()
    {
        using var host = new TestHost();
        var result = await host.ProviderRegistry.DiscoverAsync("openai", "https://api.openai.com/v1", null, "RESUCLEAN_TEST_MISSING_KEY_VAR");

        Assert.False(result.Ok);
        Assert.Empty(result.Models);
        Assert.Contains("RESUCLEAN_TEST_MISSING_KEY_VAR", result.Hint ?? string.Empty);
    }

    [Fact]
    public async Task Discovery_against_a_local_server_with_nothing_listening_fails_gracefully()
    {
        using var host = new TestHost();
        // Port 1 has nothing on it, so this proves the failure path without needing a network.
        var result = await host.ProviderRegistry.DiscoverAsync("openai", "http://127.0.0.1:1/v1", null, null);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Contains("running", result.Hint ?? string.Empty);
    }
}