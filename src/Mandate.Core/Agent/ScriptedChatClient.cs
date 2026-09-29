using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Mandate.Core.Agent;

/// <summary>
/// Deterministic stand-in for an LLM, used in tests and when no model is configured. It emits
/// real FunctionCallContent, so the whole Agent Framework / function-invocation / policy path
/// runs exactly as it does with a hosted model. It is not meant to be clever.
/// </summary>
public partial class ScriptedChatClient : IChatClient
{
    [GeneratedRegex(@"\b(?:send|transfer|pay)\s+(?:₦|n|ngn\s*)?([\d,]+(?:\.\d{1,2})?)\s*(k)?\s*(?:naira\s+)?to\s+(?:my\s+)?([a-z]+)(?:\s+for\s+(.+))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex TransferPattern();

    public int Calls;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        var list = messages.ToList();
        var last = list[^1];

        var results = last.Contents.OfType<FunctionResultContent>().ToList();
        if (results.Count > 0) return Task.FromResult(Text(Summarise(results)));

        var text = last.Text;
        var calls = new List<AIContent>();
        foreach (Match m in TransferPattern().Matches(text))
        {
            var amount = m.Groups[1].Value.Replace(",", "");
            if (m.Groups[2].Success) amount = (decimal.Parse(amount) * 1000).ToString("0.##");
            calls.Add(Call("propose_transfer", new()
            {
                ["beneficiary_alias"] = m.Groups[3].Value.ToLowerInvariant(),
                ["amount"] = amount,
                ["narration"] = m.Groups[4].Success ? m.Groups[4].Value.Trim().TrimEnd('.') : null,
            }));
        }
        if (calls.Count == 0)
        {
            var lower = text.ToLowerInvariant();
            if (lower.Contains("limit")) calls.Add(Call("get_limits", new()));
            else if (lower.Contains("beneficiar") || lower.Contains("who can")) calls.Add(Call("list_beneficiaries", new()));
            else if (lower.Contains("transaction") || lower.Contains("last")) calls.Add(Call("recent_transactions", new() { ["limit"] = 5 }));
        }

        return Task.FromResult(calls.Count > 0
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant, calls)) { ModelId = "scripted" }
            : Text("I can send money to your saved beneficiaries, show your limits, or look up recent transactions."));
    }

    private static string Summarise(List<FunctionResultContent> results)
    {
        var lines = new List<string>();
        foreach (var r in results)
        {
            using var doc = JsonDocument.Parse(r.Result?.ToString() ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err)) lines.Add(err.GetString()!);
            else if (root.TryGetProperty("status", out var status) && root.TryGetProperty("to", out var to))
                lines.Add($"{root.GetProperty("amount").GetString()} to {to.GetString()}: {status.GetString()}. " +
                          (root.TryGetProperty("instruction", out var i) ? i.GetString() : ""));
            else if (root.TryGetProperty("remainingToday", out var rem))
                lines.Add($"You have {rem.GetString()} left today. Transfers above {root.GetProperty("approvalNeededAbove").GetString()} need your approval.");
            else if (root.TryGetProperty("beneficiaries", out var b))
                lines.Add("Saved beneficiaries: " + string.Join(", ", b.EnumerateArray().Select(x => x.GetProperty("alias").GetString())));
            else if (root.TryGetProperty("transactions", out var t))
                lines.Add($"You have {t.GetArrayLength()} recent transactions.");
        }
        return string.Join(" ", lines);
    }

    private static FunctionCallContent Call(string name, Dictionary<string, object?> args) =>
        new("call_" + Guid.NewGuid().ToString("N")[..8], name, args);

    private static ChatResponse Text(string s) => new(new ChatMessage(ChatRole.Assistant, s)) { ModelId = "scripted" };

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var u in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return u;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
