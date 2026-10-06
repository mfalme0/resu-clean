using ResuClean.Models;

namespace ResuClean.Services.Providers;

/// <summary>
/// Built-in provider presets. Every base URL and model-list endpoint here was checked against the
/// provider's own documentation; see docs/MODELS.md for the source links and the date checked.
///
/// A preset only fills in the form. Nothing is saved and no key is required until you save it.
/// </summary>
public sealed record ProviderPreset(
    string Id,
    string Name,
    string Type,
    string BaseUrl,
    string EnvVar,
    bool Keyless,
    string KeyHint,
    string DocsUrl,
    string Notes,
    string[] SuggestedModels);

public static class ProviderPresets
{
    public static readonly ProviderPreset[] All =
    {
        new(
            Id: "openai",
            Name: "OpenAI",
            Type: "openai",
            BaseUrl: "https://api.openai.com/v1",
            EnvVar: "OPENAI_API_KEY",
            Keyless: false,
            KeyHint: "sk-...",
            DocsUrl: "https://platform.openai.com/api-keys",
            Notes: "The reference provider. Chat completions at {base}/chat/completions, models at {base}/models.",
            SuggestedModels: new[] { "gpt-4o-mini", "gpt-4o", "gpt-4.1-mini" }),

        new(
            Id: "anthropic",
            Name: "Anthropic",
            Type: "anthropic",
            BaseUrl: "https://api.anthropic.com",
            EnvVar: "ANTHROPIC_API_KEY",
            Keyless: false,
            KeyHint: "sk-ant-...",
            DocsUrl: "https://console.anthropic.com/settings/keys",
            Notes: "Uses the native Messages API with the x-api-key header. Models at {base}/v1/models.",
            SuggestedModels: new[] { "claude-sonnet-4-5", "claude-haiku-4-5" }),

        new(
            Id: "openrouter",
            Name: "OpenRouter",
            Type: "openai",
            BaseUrl: "https://openrouter.ai/api/v1",
            EnvVar: "OPENROUTER_API_KEY",
            Keyless: false,
            KeyHint: "sk-or-v1-...",
            DocsUrl: "https://openrouter.ai/keys",
            Notes: "One key, many providers and models. Models at {base}/models, so the list is large.",
            SuggestedModels: new[] { "anthropic/claude-sonnet-4.5", "google/gemini-2.5-pro", "openai/gpt-4o-mini" }),

        new(
            Id: "groq",
            Name: "Groq",
            Type: "openai",
            BaseUrl: "https://api.groq.com/openai/v1",
            EnvVar: "GROQ_API_KEY",
            Keyless: false,
            KeyHint: "gsk_...",
            DocsUrl: "https://console.groq.com/keys",
            Notes: "Very fast inference on open models. Models at {base}/models.",
            SuggestedModels: new[] { "llama-3.3-70b-versatile", "llama-3.1-8b-instant" }),

        new(
            Id: "gemini",
            Name: "Google Gemini",
            Type: "openai",
            BaseUrl: "https://generativelanguage.googleapis.com/v1beta/openai",
            EnvVar: "GEMINI_API_KEY",
            Keyless: false,
            KeyHint: "AIza...",
            DocsUrl: "https://aistudio.google.com/apikey",
            Notes: "Google's official OpenAI-compatible endpoint. Model ids come back as 'models/gemini-...'; resu-clean strips the prefix and hides non-chat models.",
            SuggestedModels: new[] { "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.0-flash" }),

        new(
            Id: "nvidia",
            Name: "NVIDIA NIM",
            Type: "openai",
            BaseUrl: "https://integrate.api.nvidia.com/v1",
            EnvVar: "NVIDIA_API_KEY",
            Keyless: false,
            KeyHint: "nvapi-...",
            DocsUrl: "https://build.nvidia.com/settings",
            Notes: "Hosted NVIDIA NIM endpoints, OpenAI chat compatible. Models at {base}/models.",
            SuggestedModels: new[] { "meta/llama-3.3-70b-instruct", "nvidia/llama-3.1-nem-70b-instruct" }),

        new(
            Id: "cleanapis",
            Name: "Clean APIs",
            Type: "openai",
            BaseUrl: "https://cleanapis.com/v1",
            EnvVar: "CLEANAPIS_API_KEY",
            Keyless: false,
            KeyHint: "cc_...",
            DocsUrl: "https://cleanapis.com/docs/getting-started",
            Notes: "Aggregator with one OpenAI-compatible endpoint across many vendors. Keys need the models:read scope for the model list to work.",
            SuggestedModels: new[] { "claude-opus-4.8", "gpt-4o-mini" }),

        new(
            Id: "ollama",
            Name: "Ollama (local)",
            Type: "openai",
            BaseUrl: "http://127.0.0.1:11434/v1",
            EnvVar: "",
            Keyless: true,
            KeyHint: "no key needed",
            DocsUrl: "https://ollama.com/library",
            Notes: "Runs models on this machine. Start Ollama first; resu-clean lists whatever you have pulled.",
            SuggestedModels: new[] { "llama3.2", "qwen2.5" }),

        new(
            Id: "lmstudio",
            Name: "LM Studio (local)",
            Type: "openai",
            BaseUrl: "http://127.0.0.1:1234/v1",
            EnvVar: "",
            Keyless: true,
            KeyHint: "no key needed",
            DocsUrl: "https://lmstudio.ai",
            Notes: "Local server with a developer tab. Start it and load a model before fetching the list.",
            SuggestedModels: new[] { "local-model" })
    };

    public static IReadOnlyList<ProviderPresetDto> AsDtos() =>
        All.Select(p => new ProviderPresetDto(
                p.Id, p.Name, p.Type, p.BaseUrl, p.EnvVar, p.Keyless, p.KeyHint, p.DocsUrl, p.Notes, p.SuggestedModels))
            .ToList();

    public static ProviderPreset? Find(string id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}