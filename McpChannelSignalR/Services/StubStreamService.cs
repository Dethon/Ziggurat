using Domain.DTOs.Channel;

namespace McpChannelSignalR.Services;

public sealed class StubStreamService(ILogger<StubStreamService> logger) : IStreamService
{
    public Task WriteReplyAsync(SendReplyParams p)
    {
        logger.LogDebug(
            "WriteReply: conversation={ConversationId}, type={ContentType}, complete={IsComplete}, length={Length}",
            p.ConversationId, p.ContentType, p.IsComplete, p.Content.Length);
        return Task.CompletedTask;
    }

    public ReplyReceipt ReceiptFor(SendReplyParams p) => ReplyReceipt.None;
}