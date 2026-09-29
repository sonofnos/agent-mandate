using System.Diagnostics;
using System.Text.Json;
using Mandate.Core.Domain;
using Mandate.Core.Persistence;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Mandate.Core.Agent;

public record ChatTurn(string Role, string Text);

public record AgentReply(Guid RunId, string TurnId, string Reply, IReadOnlyList<ToolCallRecord> ToolCalls, IReadOnlyList<Guid> ProposalIds,
    string Model, string PromptVersion, string? Error);

public record ModelInfo(string Model, int MaxToolIterations = 6);

/// <summary>
/// One user turn: Microsoft Agent Framework's ChatClientAgent over an IChatClient pipeline with a
/// bounded function-invocation loop. The run, including every tool call and its result, is
/// stored so any proposal can be traced back to the exact model output that caused it.
/// </summary>
public class AgentRunner(MandateDb db, ProposalService proposals, IChatClient model, ModelInfo info, ILoggerFactory loggers)
{
    public static readonly ActivitySource Source = new("AgentMandate.Agent");

    public async Task<AgentReply> RunAsync(string userId, string agentId, string message, string? turnId,
        IReadOnlyList<ChatTurn>? history, CancellationToken ct = default)
    {
        // The client sends a stable turn id and re-sends it on retry. A retried turn replays
        // into the same proposals instead of creating new ones.
        turnId = string.IsNullOrWhiteSpace(turnId) ? Guid.NewGuid().ToString("N") : turnId.Trim();
        var run = new AgentRun { UserId = userId, TurnId = turnId, Model = info.Model, PromptVersion = Prompts.Version, Input = message };
        var tools = new AgentTools(db, proposals, new AgentContext(userId, agentId, turnId, run.Id));

        using var activity = Source.StartActivity("agent.turn");
        activity?.SetTag("agent.run_id", run.Id);
        activity?.SetTag("agent.turn_id", turnId);
        activity?.SetTag("gen_ai.request.model", info.Model);

        var client = new ChatClientBuilder(model)
            .UseFunctionInvocation(loggers, f =>
            {
                f.MaximumIterationsPerRequest = info.MaxToolIterations;
                f.AllowConcurrentInvocation = false; // tools share one DbContext
                f.MaximumConsecutiveErrorsPerRequest = 2;
            })
            .UseOpenTelemetry(loggers, "AgentMandate.GenAI")
            .Build();

        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "payments-assistant",
            UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions { Instructions = Prompts.System, Tools = tools.AsAITools(), Temperature = 0 },
        }, loggers);

        var messages = (history ?? []).TakeLast(10)
            .Select(h => new ChatMessage(h.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, h.Text))
            .Append(new ChatMessage(ChatRole.User, message)).ToList();

        var sw = Stopwatch.StartNew();
        string reply;
        try
        {
            var response = await agent.RunAsync(messages, cancellationToken: ct);
            reply = response.Text;
            run.InputTokens = response.Usage?.InputTokenCount;
            run.OutputTokens = response.Usage?.OutputTokenCount;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            run.Error = e.GetType().Name + ": " + e.Message;
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            reply = await FallbackReplyAsync(tools.ProposalIds, CancellationToken.None);
        }

        run.Output = reply;
        run.LatencyMs = (int)sw.ElapsedMilliseconds;
        run.ToolCallsJson = JsonSerializer.Serialize(tools.Calls, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        return new AgentReply(run.Id, turnId, reply, tools.Calls, tools.ProposalIds.Distinct().ToList(), info.Model, Prompts.Version, run.Error);
    }

    /// <summary>
    /// When the model call fails mid-turn, tools may already have created proposals, and an
    /// auto-approved one may already be executing. The reply is built from the database, not
    /// from what we hoped happened. (The first version said "Nothing was sent" here while a
    /// transfer it had just proposed went through.)
    /// </summary>
    private async Task<string> FallbackReplyAsync(List<Guid> proposalIds, CancellationToken ct)
    {
        if (proposalIds.Count == 0) return "Sorry, something went wrong on my side. No transfer was proposed.";
        var ids = proposalIds.Distinct().ToList();
        var rows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.Proposals.Where(p => ids.Contains(p.Id)), ct);
        var lines = rows.Select(p => $"{Money.Format(p.AmountMinor, p.Currency)} to {p.BeneficiaryName}: " + p.Status switch
        {
            ProposalStatus.AwaitingApproval => "waiting for your approval in the app.",
            ProposalStatus.Approved or ProposalStatus.Executing => "approved and being sent.",
            ProposalStatus.Denied => "not allowed. " + string.Join(" ", p.Reasons),
            var s => s + ".",
        });
        return "Something went wrong before I could finish replying, but here is where things stand: " + string.Join(" ", lines);
    }
}
