using ResuClean.Data;
using ResuClean.Models;
using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// Router fallback: a failing entry must be skipped and the next one tried, and with no working
/// entry the workflow must fall back to its deterministic result instead of failing.
/// </summary>
public class RouterFallbackTests : IDisposable
{
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    /// <summary>
    /// Adds a provider pointed at a dead local port. Model discovery is skipped because the
    /// helper always supplies an explicit model list.
    /// </summary>
    private async Task<ProviderDto> AddProvider(string name, params string[] models)
    {
        return await _host.ProviderRegistry.Save(new UpsertProviderRequest
        {
            Name = name,
            Type = "openai",
            BaseUrl = "http://127.0.0.1:1/v1",
            ApiKey = "test-key-not-real",
            Models = models.ToList()
        });
    }

    private void SetRoute(string workflow, params (string ProviderId, string Model)[] entries) =>
        _host.Router.SetRoute(workflow, new SetRouteRequest(
            entries.Select(e => new RouteEntryRequest(e.ProviderId, e.Model)).ToList()));

    [Fact]
    public void With_no_route_the_workflow_reports_no_model()
    {
        var result = _host.Router.RunAsync("clean", "system", "user").GetAwaiter().GetResult();

        Assert.False(result.UsedModel);
        Assert.Equal(string.Empty, result.Text);
        Assert.Contains(result.Attempts, a => a.Contains("No route configured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_first_working_entry_wins()
    {
        var provider = await AddProvider("primary", "good-model");
        SetRoute("clean", (provider.Id, "good-model"));

        var calls = 0;
        var result = await _host.Router.RunAsync("clean", "system", "user", (_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(new ModelRouter.CallResult("rewritten"));
        });

        Assert.True(result.UsedModel);
        Assert.Equal("rewritten", result.Text);
        Assert.Equal("good-model", result.Model);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_failing_entry_falls_through_to_the_next()
    {
        var first = await AddProvider("flaky", "bad-model");
        var second = await AddProvider("backup", "good-model");
        SetRoute("clean", (first.Id, "bad-model"), (second.Id, "good-model"));

        var attempted = new List<string>();
        var result = await _host.Router.RunAsync("clean", "system", "user", (provider, model, _, _) =>
        {
            attempted.Add($"{provider.Name}/{model}");
            if (provider.Name == "flaky") throw new HttpRequestException("401 Unauthorized");
            return Task.FromResult(new ModelRouter.CallResult("from the backup"));
        });

        Assert.True(result.UsedModel);
        Assert.Equal("from the backup", result.Text);
        Assert.Equal("backup", result.Provider);
        Assert.Equal(new[] { "flaky/bad-model", "backup/good-model" }, attempted);
        Assert.Contains(result.Attempts, a => a.Contains("401", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_empty_reply_is_treated_as_a_failure_and_falls_through()
    {
        var first = await AddProvider("empty", "silent-model");
        var second = await AddProvider("chatty", "good-model");
        SetRoute("clean", (first.Id, "silent-model"), (second.Id, "good-model"));

        var result = await _host.Router.RunAsync("clean", "system", "user", (provider, _, _, _) =>
            Task.FromResult(new ModelRouter.CallResult(provider.Name == "empty" ? "   " : "real text")));

        Assert.True(result.UsedModel);
        Assert.Equal("real text", result.Text);
        Assert.Contains(result.Attempts, a => a.Contains("empty reply", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task When_every_entry_fails_the_workflow_reports_no_model_and_says_why()
    {
        var provider = await AddProvider("broken", "model");
        SetRoute("ats", (provider.Id, "model"));

        var result = await _host.Router.RunAsync("ats", "system", "user", (_, _, _, _) =>
            throw new HttpRequestException("500 Internal Server Error"));

        Assert.False(result.UsedModel);
        Assert.Equal(string.Empty, result.Text);
        Assert.NotEmpty(result.Attempts);
    }

    [Fact]
    public async Task A_disabled_provider_is_skipped_without_an_attempt()
    {
        var disabled = await AddProvider("disabled", "model");
        var enabled = await AddProvider("enabled", "model");
        await _host.ProviderRegistry.Save(new UpsertProviderRequest
        {
            Id = disabled.Id,
            Name = "disabled",
            Type = "openai",
            BaseUrl = "http://127.0.0.1:1/v1",
            ApiKey = "x",
            Models = new List<string> { "model" },
            Enabled = false
        });
        SetRoute("clean", (disabled.Id, "model"), (enabled.Id, "model"));

        var attempted = new List<string>();
        var result = await _host.Router.RunAsync("clean", "system", "user", (provider, _, _, _) =>
        {
            attempted.Add(provider.Name);
            return Task.FromResult(new ModelRouter.CallResult("ok"));
        });

        Assert.Equal(new[] { "enabled" }, attempted);
        Assert.Contains(result.Attempts, a => a.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Removing_a_provider_drops_its_route_entries()
    {
        // Foreign keys cascade, so a route entry can never outlive its provider. The router
        // therefore only ever sees a valid id, and falls back to the remaining entries.
        var doomed = await AddProvider("doomed", "model");
        var keeper = await AddProvider("keeper", "model");
        SetRoute("clean", (doomed.Id, "model"), (keeper.Id, "model"));
        _host.ProviderRegistry.Delete(doomed.Id);

        var route = _host.Router.GetRoute("clean");
        Assert.Single(route.Entries);
        Assert.Equal(keeper.Id, route.Entries[0].ProviderId);

        var result = await _host.Router.RunAsync("clean", "system", "user", (_, _, _, _) =>
            Task.FromResult(new ModelRouter.CallResult("survived")));
        Assert.True(result.UsedModel);
        Assert.Equal("survived", result.Text);
    }

    [Fact]
    public void Setting_a_route_replaces_the_previous_order()
    {
        var a = AddProvider("a", "m").GetAwaiter().GetResult();
        var b = AddProvider("b", "m").GetAwaiter().GetResult();

        SetRoute("clean", (a.Id, "m"), (b.Id, "m"));
        Assert.Equal(2, _host.Router.GetRoute("clean").Entries.Count);

        SetRoute("clean", (b.Id, "m"));
        var route = _host.Router.GetRoute("clean");
        Assert.Single(route.Entries);
        Assert.Equal(b.Id, route.Entries[0].ProviderId);
        Assert.Equal(0, route.Entries[0].Ordinal);
    }

    [Fact]
    public void Unknown_workflows_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => _host.Router.GetRoute("not_a_workflow"));
        Assert.Throws<ArgumentException>(() => _host.Router.SetRoute("nope", new SetRouteRequest(new List<RouteEntryRequest>())));
    }

    [Fact]
    public void Every_documented_workflow_is_available()
    {
        foreach (var workflow in ModelRouter.Workflows)
        {
            Assert.Equal(workflow, _host.Router.GetRoute(workflow).Workflow);
        }
    }

    [Fact]
    public void The_system_prompt_forbids_fabrication_in_every_workflow()
    {
        foreach (var workflow in ModelRouter.Workflows)
        {
            var prompt = ModelRouter.SystemPrompt(workflow, "VERIFIED FACTS:\n- Led a team");
            Assert.Contains("Never invent", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("VERIFIED FACTS", prompt);
        }
    }

    [Fact]
    public void No_endpoint_exposes_an_api_key()
    {
        var provider = _host.ProviderRegistry.Save(new UpsertProviderRequest
        {
            Name = "secret-holder",
            Type = "openai",
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-super-secret-value",
            Models = new List<string> { "gpt-4o-mini" }
        }).GetAwaiter().GetResult();

        Assert.True(provider.HasKey);
        Assert.Equal("stored", provider.KeySource);
        // The DTO carries no key field at all: only the boolean and the source.
        Assert.DoesNotContain(
            typeof(ProviderDto).GetProperties().Select(p => p.Name),
            name => name.Contains("key", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("has", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Keys_round_trip_through_encryption_at_rest()
    {
        const string secret = "sk-test-1234567890abcdef";
        var saved = _host.ProviderRegistry.Save(new UpsertProviderRequest
        {
            Name = "roundtrip",
            Type = "openai",
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = secret,
            Models = new List<string> { "gpt-4o-mini" }
        }).GetAwaiter().GetResult();

        var resolved = _host.ProviderRegistry.ResolveKey(saved);
        Assert.Equal(secret, resolved.Value);
        Assert.Equal("stored", resolved.Source);

        // Release pooled connections so the database file can be read directly.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var asText = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_host.DataDir, "resu-clean.db")));
        Assert.DoesNotContain(secret, asText);
    }

    [Fact]
    public void Saving_without_a_key_keeps_the_stored_one()
    {
        var saved = _host.ProviderRegistry.Save(new UpsertProviderRequest
        {
            Name = "keeper",
            Type = "openai",
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-original-key-value",
            Models = new List<string> { "a" }
        }).GetAwaiter().GetResult();

        _host.ProviderRegistry.Save(new UpsertProviderRequest
        {
            Id = saved.Id,
            Name = "keeper",
            BaseUrl = "https://api.openai.com/v1",
            Models = new List<string> { "a", "b" }
        }).GetAwaiter().GetResult();

        var reloaded = _host.ProviderRegistry.Get(saved.Id)!;
        Assert.Equal("sk-original-key-value", _host.ProviderRegistry.ResolveKey(reloaded).Value);
    }
}

/// <summary>
/// Three properties of a model call that are invisible when they are wrong: that malformed output
/// is treated as a routing failure rather than parsed into nothing, that an identical question is
/// only asked once, and that what the call cost is recorded rather than discarded.
/// </summary>
public class RouterCacheAndSchemaTests : IDisposable
{
    private readonly TestHost _host = new(cacheTtlSeconds: 900);

    public void Dispose() => _host.Dispose();

    private async Task<ProviderDto> AddProvider(string name, params string[] models) =>
        await _host.ProviderRegistry.Save(new UpsertProviderRequest
        {
            Name = name,
            Type = "openai",
            BaseUrl = "http://127.0.0.1:1/v1",
            ApiKey = "test-key-not-real",
            Models = models.ToList()
        });

    private void SetRoute(string workflow, params (string ProviderId, string Model)[] entries) =>
        _host.Router.SetRoute(workflow, new SetRouteRequest(
            entries.Select(e => new RouteEntryRequest(e.ProviderId, e.Model)).ToList()));

    private sealed record Claim(string Text, string FactId);

    /// <summary>Stands in for a model that ignored the JSON instruction and answered in prose.</summary>
    private static List<Claim>? ParseClaims(string reply) =>
        System.Text.Json.JsonSerializer.Deserialize<List<Claim>>(reply) is { Count: > 0 } claims ? claims : null;

    [Fact]
    public async Task A_reply_that_is_not_the_expected_shape_is_a_routing_failure()
    {
        var first = await AddProvider("prose", "chatty");
        var second = await AddProvider("strict", "json");
        SetRoute("ats", (first.Id, "chatty"), (second.Id, "json"));

        var attempted = new List<string>();
        async Task<ModelRouter.CallResult> Reply(ProviderDto provider, string model, string sys, CancellationToken token)
        {
            attempted.Add(provider.Name);
            // The first provider ignores the JSON instruction and answers in prose.
            return new ModelRouter.CallResult(provider.Name == "prose"
                ? "Sure! The first claim is fine and the second looks made up."
                : """[{"text":"Reduced costs 20%","factId":"fact_abc"}]""");
        }

        var result = await _host.Router.RunStructuredAsync("ats", "system", "user", ParseClaims, Reply);

        Assert.True(result.Routed.UsedModel);
        Assert.Equal("strict", result.Routed.Provider);
        Assert.Single(result.Value!);
        // The malformed reply from the first entry was recorded, then skipped rather than accepted.
        Assert.Contains(result.Routed.Attempts, a => a.Contains("expected shape", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "prose", "strict" }, attempted);
    }

    [Fact]
    public async Task A_structured_run_yields_nothing_when_every_reply_is_malformed()
    {
        var only = await AddProvider("prose", "chatty");
        SetRoute("ats", (only.Id, "chatty"));

        async Task<ModelRouter.CallResult> Prose(ProviderDto p, string m, string s, CancellationToken t) =>
            new("Here are some thoughts about your resume.");

        var result = await _host.Router.RunStructuredAsync("ats", "system", "user", ParseClaims, Prose);

        // No model result, so the caller falls back to its deterministic path rather than
        // reporting an empty analysis as though the model had returned it.
        Assert.False(result.Routed.UsedModel);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task An_identical_question_is_asked_only_once()
    {
        var provider = await AddProvider("local", "ollama-model");
        SetRoute("clean", (provider.Id, "ollama-model"));

        var calls = 0;
        async Task<ModelRouter.CallResult> Count(ProviderDto p, string m, string s, CancellationToken t)
        {
            calls++;
            return new ModelRouter.CallResult("the same answer every time");
        }

        var first = await _host.Router.RunAsync("clean", "system", "user", Count);
        var second = await _host.Router.RunAsync("clean", "system", "user", Count);

        Assert.Equal(1, calls);
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(first.Text, second.Text);
    }

    [Fact]
    public async Task A_different_question_is_not_served_from_the_cache()
    {
        var provider = await AddProvider("local", "ollama-model");
        SetRoute("clean", (provider.Id, "ollama-model"));

        var calls = 0;
        async Task<ModelRouter.CallResult> Count(ProviderDto p, string m, string s, CancellationToken t)
        {
            calls++;
            return new ModelRouter.CallResult($"answer {calls}");
        }

        await _host.Router.RunAsync("clean", "system", "user A", Count);
        await _host.Router.RunAsync("clean", "system", "user B", Count);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Bypassing_the_cache_forces_a_live_call()
    {
        var provider = await AddProvider("local", "ollama-model");
        SetRoute("clean", (provider.Id, "ollama-model"));

        var calls = 0;
        async Task<ModelRouter.CallResult> Count(ProviderDto p, string m, string s, CancellationToken t)
        {
            calls++;
            return new ModelRouter.CallResult("live");
        }

        await _host.Router.RunAsync("clean", "system", "user", Count);
        await _host.Router.RunAsync("clean", "system", "user", Count, bypassCache: true);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Caching_is_off_when_the_ttl_is_zero()
    {
        using var host = new TestHost(cacheTtlSeconds: 0);
        var provider = await host.ProviderRegistry.Save(new UpsertProviderRequest
        {
            Name = "local",
            Type = "openai",
            BaseUrl = "http://127.0.0.1:1/v1",
            ApiKey = "test-key-not-real",
            Models = new List<string> { "m" }
        });
        host.Router.SetRoute("clean", new SetRouteRequest(
            new List<RouteEntryRequest> { new(provider.Id, "m") }));

        var calls = 0;
        async Task<ModelRouter.CallResult> Count(ProviderDto p, string m, string s, CancellationToken t)
        {
            calls++;
            return new ModelRouter.CallResult("live");
        }

        await host.Router.RunAsync("clean", "system", "user", Count);
        await host.Router.RunAsync("clean", "system", "user", Count);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Token_spend_is_recorded_rather_than_discarded()
    {
        var provider = await AddProvider("metered", "some-model");
        SetRoute("update_resume", (provider.Id, "some-model"));

        async Task<ModelRouter.CallResult> Metered(ProviderDto p, string m, string s, CancellationToken t) =>
            new("tailored text", PromptTokens: 1200, CompletionTokens: 340);

        await _host.Router.RunAsync("update_resume", "system", "user", Metered);

        var byWorkflow = _host.Router.UsageByWorkflow();
        var row = Assert.Single(byWorkflow, r => r.Workflow == "update_resume");
        Assert.Equal(1540, row.Tokens);
        Assert.Equal(0, row.CachedCalls);

        var recent = _host.Router.RecentUsage();
        Assert.Contains(recent, r => r.Workflow == "update_resume" && r.PromptTokens == 1200 && !r.Cached);
    }

    [Fact]
    public async Task A_cache_hit_is_recorded_as_a_call_that_cost_nothing()
    {
        var provider = await AddProvider("local", "m");
        SetRoute("clean", (provider.Id, "m"));

        async Task<ModelRouter.CallResult> Free(ProviderDto p, string m, string s, CancellationToken t) =>
            new("cached answer");

        await _host.Router.RunAsync("clean", "system", "user", Free);
        await _host.Router.RunAsync("clean", "system", "user", Free);

        var row = Assert.Single(_host.Router.UsageByWorkflow(), r => r.Workflow == "clean");
        Assert.Equal(2, row.Calls);
        Assert.Equal(1, row.CachedCalls);
    }
}