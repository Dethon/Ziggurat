using Domain.DTOs;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;

namespace Tests.Integration.Fixtures;

// One agent turn against the fake model: no retries and no stall budget to spend, because nothing on
// the other end varies — a turn that does not land here is the code under test failing.
public static class FakeTurn
{
    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(30);

    public static OpenRouterChatClient Client(FakeOpenRouter model, string modelId = "fake/model") =>
        new(model.ApiUrl, "sk-or-fake", modelId);

    public static async Task<List<AiResponse>> RunAsync(McpAgent agent, string prompt)
    {
        using var cts = new CancellationTokenSource(_budget);
        return await agent.RunStreamingAsync(prompt, cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);
    }
}