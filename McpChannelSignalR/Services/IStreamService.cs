using Domain.DTOs.Channel;

namespace McpChannelSignalR.Services;

public interface IStreamService
{
    Task WriteReplyAsync(SendReplyParams p);

    // What send_reply answers a written reply with (ReplyReceipt).
    ReplyReceipt ReceiptFor(SendReplyParams p);
}