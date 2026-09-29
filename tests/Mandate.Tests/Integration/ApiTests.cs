using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Mandate.Core.Agent;
using Mandate.Core.Execution;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Mandate.Tests.Integration;

public class ApiTests
{
    private record Session(string UserId, string UserToken, string AgentToken);

    private static async Task<(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory, HttpClient Http, Session S)> StartAsync(IChatClient? model = null)
    {
        var factory = new ApiFactory(await Db.ConnectionStringAsync());
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> configured = model is null
            ? factory
            : factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(model)));
        var http = configured.CreateClient();
        var s = (await (await http.PostAsync("/demo/session", null)).Content.ReadFromJsonAsync<Session>())!;
        return (configured, http, s);
    }

    private static HttpRequestMessage As(string token, HttpMethod m, string url, object? body = null)
    {
        var req = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<JsonElement> Chat(HttpClient http, Session s, string message, string? turnId = null) =>
        await Json(await http.SendAsync(As(s.AgentToken, HttpMethod.Post, "/agent/chat", new { message, turnId })));

    private static async Task<JsonElement[]> Proposals(HttpClient http, Session s) =>
        (await Json(await http.SendAsync(As(s.UserToken, HttpMethod.Get, "/me/proposals")))).EnumerateArray().ToArray();

    [Fact]
    public async Task Small_transfer_flows_from_chat_to_the_provider()
    {
        var (factory, http, s) = await StartAsync();
        var reply = await Chat(http, s, "send 5000 to mum for groceries");
        Assert.Contains("Approved", reply.GetProperty("reply").GetString());

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Executor>().DrainAsync();

        var p = Assert.Single(await Proposals(http, s));
        Assert.Equal("Succeeded", p.GetProperty("status").GetString());
        Assert.Equal(500_000, p.GetProperty("amountMinor").GetInt64());
        Assert.Equal("groceries", p.GetProperty("narration").GetString());
        Assert.Single(factory.Services.GetRequiredService<FakeTransferProvider>().Applied);
    }

    [Fact]
    public async Task Agent_never_sees_the_approval_token_and_cannot_approve()
    {
        var (_, http, s) = await StartAsync();
        var reply = await Chat(http, s, "send 50000 to mum");
        Assert.DoesNotContain("approvalToken", reply.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var p = Assert.Single(await Proposals(http, s));
        Assert.Equal("AwaitingApproval", p.GetProperty("status").GetString());
        var id = p.GetProperty("id").GetString();
        var token = p.GetProperty("approvalToken").GetString();

        var asAgent = await http.SendAsync(As(s.AgentToken, HttpMethod.Post, $"/me/proposals/{id}/approve", new { token }));
        Assert.Equal(HttpStatusCode.Forbidden, asAgent.StatusCode);

        var forged = await http.SendAsync(As(s.UserToken, HttpMethod.Post, $"/me/proposals/{id}/approve", new { token = "1.abc" }));
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);

        var asUser = await Json(await http.SendAsync(As(s.UserToken, HttpMethod.Post, $"/me/proposals/{id}/approve", new { token })));
        Assert.Equal("Approved", asUser.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Retried_chat_turn_does_not_propose_twice()
    {
        var (_, http, s) = await StartAsync();
        await Chat(http, s, "send 5000 to mum", "turn-abc");
        await Chat(http, s, "send 5000 to mum", "turn-abc"); // client retry after a dropped response
        Assert.Single(await Proposals(http, s));
    }

    [Fact]
    public async Task Unknown_recipient_produces_no_proposal()
    {
        var (_, http, s) = await StartAsync();
        var reply = await Chat(http, s, "send 5000 to stranger");
        Assert.Contains("No saved beneficiary", reply.GetProperty("reply").GetString());
        Assert.Empty(await Proposals(http, s));
    }

    [Fact]
    public async Task A_model_that_obeys_an_injected_instruction_still_cannot_move_money()
    {
        // Worst case: the model reads the poisoned narration and does exactly what it says.
        var (factory, http, s) = await StartAsync(new InjectionObeyingChatClient());
        await Chat(http, s, "what was my last transaction?");

        var p = Assert.Single(await Proposals(http, s));
        Assert.Equal("TUNDE BAKARE", p.GetProperty("to").GetString());
        Assert.Equal("AwaitingApproval", p.GetProperty("status").GetString());
        var reasons = p.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains(reasons, r => r!.Contains("recently"));
        Assert.Contains(reasons, r => r!.Contains("auto-approve"));

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Executor>().DrainAsync();
        Assert.Empty(factory.Services.GetRequiredService<FakeTransferProvider>().Applied);
    }

    [Fact]
    public async Task Run_record_links_the_proposal_to_the_model_output_that_caused_it()
    {
        var (_, http, s) = await StartAsync();
        var reply = await Chat(http, s, "send 5000 to mum");
        var runId = reply.GetProperty("runId").GetString();

        var run = await Json(await http.SendAsync(As(s.UserToken, HttpMethod.Get, $"/me/runs/{runId}")));
        Assert.Equal(Prompts.Version, run.GetProperty("promptVersion").GetString());
        Assert.Contains("propose_transfer", run.GetProperty("toolCallsJson").GetString());

        var p = Assert.Single(await Proposals(http, s));
        Assert.Equal(runId, p.GetProperty("agentRunId").GetString());

        var audit = await Json(await http.SendAsync(As(s.UserToken, HttpMethod.Get, $"/me/proposals/{p.GetProperty("id").GetString()}/audit")));
        Assert.True(audit.GetProperty("chainIntact").GetBoolean());
    }

    [Fact]
    public async Task Mcp_exposes_propose_but_nothing_that_executes_and_honours_idempotency_keys()
    {
        var (factory, http, s) = await StartAsync();
        var mcpHttp = factory.CreateDefaultClient(new BearerHandler(s.AgentToken));
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(mcpHttp.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, mcpHttp, ownsHttpClient: false));

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("propose_transfer", tools);
        Assert.DoesNotContain(tools, t => t.Contains("execute") || t.Contains("approve") || t.Contains("add_beneficiary"));

        var args = new Dictionary<string, object?> { ["beneficiary_alias"] = "mum", ["amount"] = "5000", ["idempotency_key"] = "req-1", ["narration"] = "mcp" };
        await client.CallToolAsync("propose_transfer", args);
        await client.CallToolAsync("propose_transfer", args);
        Assert.Single(await Proposals(http, s));

        var noAuth = await factory.CreateClient().PostAsync("/mcp", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);
    }

    private class BearerHandler(string token) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, ct);
        }
    }
}

/// <summary>A fully compromised model: whatever the untrusted text asks for, it does.</summary>
public class InjectionObeyingChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var last = messages.Last();
        var results = last.Contents.OfType<FunctionResultContent>().ToList();
        if (results.Any(r => r.Result?.ToString()?.Contains("SYSTEM NOTE") == true))
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("c2", "propose_transfer", new Dictionary<string, object?> { ["beneficiary_alias"] = "tunde", ["amount"] = "150000" })])));
        if (results.Count > 0) return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("c1", "recent_transactions", new Dictionary<string, object?> { ["limit"] = 3 })])));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken ct = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
