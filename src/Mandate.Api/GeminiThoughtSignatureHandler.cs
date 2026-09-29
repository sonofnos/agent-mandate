using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace Mandate.Api;

/// <summary>
/// Gemini 3 on its OpenAI-compatible endpoint attaches a thought signature to each tool call
/// (tool_calls[].extra_content) and returns 400 on the follow-up request unless it is sent back
/// verbatim. The OpenAI .NET client drops unknown fields, so the second model call of every
/// tool-using turn failed. This handler remembers extra_content by tool call id from responses
/// and puts it back on the matching assistant tool_calls in the next request.
/// </summary>
public class GeminiThoughtSignatureHandler : DelegatingHandler
{
    private const int MaxEntries = 10_000;
    private readonly ConcurrentDictionary<string, (JsonNode Extra, DateTimeOffset At)> _byCallId = new();

    public GeminiThoughtSignatureHandler(HttpMessageHandler inner) : base(inner)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is not null && request.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var body = JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
            if (body?["messages"] is JsonArray messages && Reinject(messages))
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        var response = await base.SendAsync(request, ct);
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            Remember(JsonNode.Parse(text));
            var replaced = new StringContent(text, Encoding.UTF8, "application/json");
            response.Content = replaced;
        }
        return response;
    }

    private bool Reinject(JsonArray messages)
    {
        var changed = false;
        foreach (var call in messages.OfType<JsonObject>().SelectMany(m => m["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (call["extra_content"] is null && call["id"]?.GetValue<string>() is { } id && _byCallId.TryGetValue(id, out var saved))
            {
                call["extra_content"] = saved.Extra.DeepClone();
                changed = true;
            }
        }
        return changed;
    }

    private void Remember(JsonNode? response)
    {
        if (response?["choices"] is not JsonArray choices) return;
        foreach (var call in choices.SelectMany(c => c?["message"]?["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
            if (call["id"]?.GetValue<string>() is { } id && call["extra_content"] is { } extra)
                _byCallId[id] = (extra.DeepClone(), DateTimeOffset.UtcNow);

        if (_byCallId.Count > MaxEntries)
            foreach (var old in _byCallId.OrderBy(kv => kv.Value.At).Take(_byCallId.Count - MaxEntries / 2))
                _byCallId.TryRemove(old.Key, out _);
    }
}
