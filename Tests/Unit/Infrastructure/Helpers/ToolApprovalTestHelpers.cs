using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;

namespace Tests.Unit.Infrastructure.Helpers;

internal sealed class TestApprovalHandler(ToolApprovalResult result) : IToolApprovalHandler
{
    public List<IReadOnlyList<ToolApprovalRequest>> RequestedApprovals { get; } = [];
    public List<IReadOnlyList<ToolApprovalRequest>> AutoApprovedNotifications { get; } = [];
    public List<string> ConversationIds { get; } = [];

    public Task<ToolApprovalResult> RequestApprovalAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        ConversationIds.Add(conversationId);
        RequestedApprovals.Add(requests);
        return Task.FromResult(result);
    }

    public Task NotifyAutoApprovedAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        ConversationIds.Add(conversationId);
        AutoApprovedNotifications.Add(requests);
        return Task.CompletedTask;
    }
}

internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses = new();

    // What a real OpenRouter client answers once a response has streamed: the model and provider
    // that actually served it, which is not necessarily the ones configured.
    public ServedRoute? Route { get; set; }

    public void SetNextResponse(ChatResponse response) => _responses.Enqueue(response);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_responses.TryDequeue(out var response))
        {
            return Task.FromResult(response);
        }

        return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Done")])
        {
            FinishReason = ChatFinishReason.Stop
        });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => AsyncEnumerable.Empty<ChatResponseUpdate>();

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ServedRoute) ? Route : null;
}

internal static class ToolApprovalResponseFactory
{
    public static ChatResponse CreateToolCallResponse(
        string toolName, string callId, IDictionary<string, object?>? arguments = null)
    {
        var toolCallContent = new FunctionCallContent(
            callId, toolName, arguments ?? new Dictionary<string, object?>());
        var message = new ChatMessage(ChatRole.Assistant, [toolCallContent]);
        return new ChatResponse([message]) { FinishReason = ChatFinishReason.ToolCalls };
    }

    public static ChatResponse CreateMultiToolCallResponse(params (string toolName, string callId)[] tools)
    {
        var contents = tools
            .Select(t => new FunctionCallContent(t.callId, t.toolName, new Dictionary<string, object?>()))
            .ToList<AIContent>();
        var message = new ChatMessage(ChatRole.Assistant, contents);
        return new ChatResponse([message]) { FinishReason = ChatFinishReason.ToolCalls };
    }
}