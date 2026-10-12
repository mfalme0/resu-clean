using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ResuClean.Models;
using ResuClean.Services;
using ResuClean.Services.Providers;
using ResuClean.Services.Search;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// The search feature is the one place where model output is allowed to contain numbers, dates and
/// outside names, so most of these pin the boundaries that keep that from leaking into your profile.
/// </summary>
public sealed class WebSearchTests
{
    [Fact]
    public void Web_search_is_a_routable_workflow()
    {
        Assert.Contains("web_search", ModelRouter.Workflows);
    }

    [Fact]
    public void The_search_prompt_demands_citations_and_forbids_inventing_facts()
    {
        var prompt = ModelRouter.SystemPrompt("web_search", "VERIFIED FACTS:\n- Led a team");

        // It keeps the global no-fabrication banner...
        Assert.Contains("Never invent", prompt, StringComparison.OrdinalIgnoreCase);
        // ...and adds the rules that only make sense for quoted web content.
        Assert.Contains("Cite sources inline", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not in the results", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void With_no_backend_configured_search_says_what_to_do_instead_of_failing()
    {
        // No key, no route: the app must still answer, and must name the fix.
        var host = new TestHost();
        var search = new WebSearchService(host.ProviderRegistry, host.Router, new StubHttpFactory());

        var caps = search.Capabilities();
        Assert.False(caps.NativeConfigured);

        var answer = search.SearchAsync(new WebSearchRequest("test query", null), CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.Equal("none", answer.Tier);
        Assert.False(answer.UsedModel);
        Assert.Empty(answer.Results);
        Assert.Contains(answer.Notes!, n => n.Contains(WebSearchService.TavilyEnvVar));
        Assert.Contains(answer.Notes!, n => n.Contains(WebSearchService.BraveEnvVar));
    }

    [Fact]
    public void Search_capabilities_reflect_which_env_keys_are_set()
    {
        const string Var = WebSearchService.BraveEnvVar;
        var previous = Environment.GetEnvironmentVariable(Var);

        var host = new TestHost();
        var search = new WebSearchService(host.ProviderRegistry, host.Router, new StubHttpFactory());

        try
        {
            Environment.SetEnvironmentVariable(Var, "test-key");
            var withKey = search.Capabilities();
            Assert.True(withKey.SearchApiConfigured);
            Assert.Equal("Brave Search", withKey.SearchApiName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, previous);
        }
    }

    [Fact]
    public async Task No_search_operation_is_ever_written_to_the_profile()
    {
        // The architectural guarantee, asserted rather than documented: the only call sites that
        // write profile facts are the resume workflows. Search has no path to them at all.
        var searchSource = await File.ReadAllTextAsync(
            Path.Combine(FindRepoRoot(), "server", "Services", "Search", "WebSearchService.cs"));

        Assert.DoesNotContain("_profile.", searchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfileService", searchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AddFact", searchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Validate(", searchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_output_is_never_run_through_the_no_fabrication_guard()
    {
        // The guard hard-discards any number or date absent from the input. Web results are made of
        // exactly those, so routing search through it would reject every real answer.
        var guard = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "server", "Services", "ResumeWorkflows.cs"));

        // The guard exists and is used by the resume workflows...
        Assert.Contains("InventedTokens", guard, StringComparison.Ordinal);
        // ...but the search workflow is not one of them.
        Assert.DoesNotContain("web_search", guard, StringComparison.OrdinalIgnoreCase);
    }

    // ---- provider payload shapes -------------------------------------------

    [Fact]
    public void The_openai_payload_only_carries_search_options_when_search_is_requested()
    {
        var withoutSearch = BuildOpenAiPayload(webSearch: false);
        var withSearch = BuildOpenAiPayload(webSearch: true);

        Assert.False(withoutSearch.TryGetProperty("web_search_options", out _));
        Assert.True(withSearch.TryGetProperty("web_search_options", out var opts));
        Assert.Equal("medium", opts.GetProperty("search_context_size").GetString());

        // Everything else must stay byte-identical so existing workflows are untouched.
        Assert.Equal(
            withoutSearch.GetProperty("model").GetString(),
            withSearch.GetProperty("model").GetString());
    }

    [Fact]
    public void The_anthropic_payload_adds_the_bounded_web_search_tool()
    {
        var withoutTool = BuildAnthropicPayload(webSearch: false);
        var withTool = BuildAnthropicPayload(webSearch: true);

        Assert.False(withoutTool.TryGetProperty("tools", out _));
        Assert.True(withTool.TryGetProperty("tools", out var tools));

        var tool = tools.EnumerateArray().Single();
        Assert.Equal("web_search_20250305", tool.GetProperty("type").GetString());
        // Bounded so one query cannot turn into an unbounded bill.
        Assert.True(tool.GetProperty("max_uses").GetInt32() is >= 1 and <= 10);
    }

    [Fact]
    public void Citations_are_read_out_of_an_openai_search_reply()
    {
        const string body = """
        {
          "choices": [{
            "web_search_call": { "status": "completed" },
            "message": {
              "content": "Answer text.",
              "annotations": [
                { "type": "url_citation", "url_citation": { "url": "https://a.example/1" } },
                { "type": "url_citation", "url_citation": { "url": "https://b.example/2" } },
                { "type": "url_citation", "url_citation": { "url": "https://a.example/1" } }
              ]
            }
          }],
          "usage": { "prompt_tokens": 10, "completion_tokens": 5 }
        }
        """;

        var reply = ReadOpenAi(body);

        Assert.True(reply.UsedNativeSearch);
        Assert.Equal(2, reply.Citations!.Count);          // de-duplicated
        Assert.Equal("https://a.example/1", reply.Citations[0]);
    }

    [Fact]
    public void A_plain_reply_is_reported_as_not_having_searched()
    {
        const string body = """
        {
          "choices": [{ "message": { "content": "From memory." } }],
          "usage": { "prompt_tokens": 10, "completion_tokens": 5 }
        }
        """;

        var reply = ReadOpenAi(body);

        Assert.False(reply.UsedNativeSearch);
        Assert.Equal("From memory.", reply.Text);
    }

    [Fact]
    public void Citations_are_read_out_of_an_anthropic_search_reply()
    {
        const string body = """
        {
          "content": [
            { "type": "server_tool_use", "name": "web_search" },
            { "type": "text", "text": "Answer text." },
            { "type": "citations", "cited_text": "a claim", "url": "https://c.example/3" }
          ],
          "usage": { "input_tokens": 10, "output_tokens": 5 }
        }
        """;

        var reply = ReadAnthropic(body);

        Assert.True(reply.UsedNativeSearch);
        Assert.Equal("Answer text.", reply.Text);
        Assert.Contains("https://c.example/3", reply.Citations!);
    }

    // ---- helpers ------------------------------------------------------------

    private static JsonElement BuildOpenAiPayload(bool webSearch) =>
        Capture(new ProviderDto(
            "p", "Test", "openai", "https://api.test.local/v1", "", false, "none",
            Array.Empty<string>(), true, "", false), webSearch);

    private static JsonElement BuildAnthropicPayload(bool webSearch) =>
        Capture(new ProviderDto(
            "p", "Test", "anthropic", "https://api.test.local", "", false, "none",
            Array.Empty<string>(), true, "", false), webSearch);

    /// <summary>
    /// Round-trips a payload through the real builder over a real socket, so the test asserts what
    /// would actually go on the wire. 127.0.0.1 counts as keyless, so no key is needed.
    /// </summary>
    private static JsonElement Capture(ProviderDto provider, bool webSearch)
    {
        using var server = new OneShotServer("{}");

        var withPort = provider with { BaseUrl = $"{server.BaseUrl}{(provider.Type == "anthropic" ? "" : "/v1")}" };
        var service = NewService();

        try
        {
            service.CallAsync(withPort, "some-model", "system", "user", CancellationToken.None, webSearch)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // The stub returns a body the parser cannot use; we only want the captured request.
        }

        Assert.NotNull(server.CapturedBody);
        return JsonDocument.Parse(server.CapturedBody!).RootElement.Clone();
    }

    private static ProviderService.ModelReply ReadOpenAi(string body) =>
        Read("openai", body);

    private static ProviderService.ModelReply ReadAnthropic(string body) =>
        Read("anthropic", body);

    private static ProviderService.ModelReply Read(string type, string body)
    {
        using var server = new OneShotServer(body);

        var baseUrl = type == "anthropic" ? server.BaseUrl : $"{server.BaseUrl}/v1";
        var provider = new ProviderDto(
            "p", "Test", type, baseUrl, "", false, "none",
            Array.Empty<string>(), true, "", false);

        return NewService().CallAsync(provider, "m", "s", "u").GetAwaiter().GetResult();
    }

    private static ProviderService NewService() =>
        new(new ResuClean.Data.Db(new Configuration.AppConfig()), new ConfigurationBuilder().Build());

    /// <summary>
    /// A single-request HTTP server on loopback. ProviderService builds its own HttpClient, so the
    /// only honest way to see the payload it would send is to let it send it somewhere.
    /// </summary>
    private sealed class OneShotServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _response;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string? CapturedBody { get; private set; }
        public string BaseUrl { get; }

        public OneShotServer(string response)
        {
            _response = response;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _loop = Task.Run(AcceptOnceAsync);
        }

        private async Task AcceptOnceAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

                var contentLength = 0;
                string? line;
                var bodyLines = new List<string>();

                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_cts.Token)))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                    else if (line.Length == 0)
                        break;
                }

                if (contentLength > 0)
                {
                    var buffer = new char[contentLength];
                    var read = 0;
                    while (read < contentLength)
                    {
                        var n = await reader.ReadAsync(buffer.AsMemory(read, contentLength - read), _cts.Token);
                        if (n == 0) break;
                        read += n;
                    }
                    bodyLines.Add(new string(buffer, 0, read));
                }

                CapturedBody = string.Concat(bodyLines);

                var payload = Encoding.UTF8.GetBytes(_response);
                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                    + payload.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, _cts.Token);
                await stream.WriteAsync(payload, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* the assertion reports what was missing */ }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }
    }

    /// <summary>Stand-in for the DI HttpClientFactory. Never reached in these tests, but must exist.</summary>
    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new CaptureHandler(_ => "{}"));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<string>> _body;
        public CaptureHandler(Func<HttpRequestMessage, string> body)
            => _body = r => Task.FromResult(body(r));
        public CaptureHandler(Func<HttpRequestMessage, Task<string>> body) => _body = body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await _body(request);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "server", "ResuClean.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
