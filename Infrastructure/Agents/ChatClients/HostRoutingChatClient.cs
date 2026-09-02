using Domain.Agents;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

// One client per agent, two hosts behind it. A turn goes to the Lemonade chat host when the
// model it resolved to carries the host's prefix, and to OpenRouter otherwise — through exactly
// the path it goes through today. The decision reads the turn's own options, so a concurrent turn
// on the other host has nothing shared to overwrite.
public sealed class HostRoutingChatClient(IChatClient openRouter, IChatClient lemonade) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        For(options).GetResponseAsync(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        For(options).GetStreamingResponseAsync(messages, options, cancellationToken);

    // Anything asked of the client — metadata, the served route — is answered by the OpenRouter
    // side, which is the one every existing caller was built against.
    public object? GetService(Type serviceType, object? key = null) =>
        serviceType.IsInstanceOfType(this) ? this : openRouter.GetService(serviceType, key);

    public void Dispose()
    {
        openRouter.Dispose();
        lemonade.Dispose();
    }

    private IChatClient For(ChatOptions? options) =>
        LemonadeModelId.IsLemonade(options?.ModelId) ? lemonade : openRouter;
}