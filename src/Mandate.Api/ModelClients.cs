using System.ClientModel;
using Mandate.Core.Agent;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Mandate.Api;

public static class ModelClients
{
    /// <summary>
    /// Any OpenAI-compatible endpoint (OpenAI, Azure OpenAI, Gemini, Groq, a LiteLLM proxy).
    /// With no key configured the service runs on <see cref="ScriptedChatClient"/>, so the demo
    /// and CI need no secrets.
    /// </summary>
    public static (IChatClient Client, ModelInfo Info) Create(IConfiguration config)
    {
        var key = config["LLM:ApiKey"];
        var models = (config["LLM:Models"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (string.IsNullOrEmpty(key) || models.Length == 0) return (new ScriptedChatClient(), new ModelInfo("scripted"));

        var endpoint = new Uri(config["LLM:BaseUrl"] ?? "https://api.openai.com/v1/");
        var options = new OpenAIClientOptions { Endpoint = endpoint, NetworkTimeout = TimeSpan.FromSeconds(45) };
        if (endpoint.Host.EndsWith("googleapis.com", StringComparison.OrdinalIgnoreCase))
            options.Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(
                new HttpClient(new GeminiThoughtSignatureHandler(new SocketsHttpHandler())));
        var openai = new OpenAIClient(new ApiKeyCredential(key), options);
        var clients = models.Select(m => openai.GetChatClient(m).AsIChatClient()).ToList();
        return (clients.Count == 1 ? clients[0] : new FallbackChatClient(clients), new ModelInfo(string.Join(">", models)));
    }
}

/// <summary>Tries each model in order when one is rate limited or unavailable.</summary>
public class FallbackChatClient(IReadOnlyList<IChatClient> clients) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var list = messages.ToList();
        for (var i = 0; ; i++)
        {
            try
            {
                return await clients[i].GetResponseAsync(list, options, ct);
            }
            catch (ClientResultException e) when (i < clients.Count - 1 && e.Status is 429 or >= 500)
            {
            }
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken ct = default) => clients[0].GetStreamingResponseAsync(messages, options, ct);

    public object? GetService(Type serviceType, object? serviceKey = null) => clients[0].GetService(serviceType, serviceKey);

    public void Dispose()
    {
        foreach (var c in clients) c.Dispose();
    }
}
