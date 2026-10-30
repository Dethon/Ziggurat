using Domain.DTOs.WebChat;

namespace McpChannelTelegram.Services;

// How this channel spells an attachment reference, and reads one back. A Telegram file handle is
// per-bot, so the reference names the agent whose bot can fetch it, which also scopes it for free:
// another agent's bot cannot use the handle. It begins with the conversation's directory, as every
// channel's reference does, because a review copy keeps the file on the review host under this very
// id, and that store files a copy under the conversation it belongs to (ADR 0046). The agent and
// the file share the second segment, joined by a dot, which a file id (base64url) never holds.
public static class TelegramReference
{
    public static string For(string conversationId, string agentId, string fileId) =>
        $"{AttachmentEndpointPaths.ConversationDirectory(conversationId)}/{agentId}.{fileId}";

    // The agent and the file a reference names, or null when it names neither. A reference minted
    // before references named their conversation, `<agentId>/<fileId>`, is still in histories and
    // still reads: its second segment holds no dot.
    public static (string AgentId, string FileId)? Parse(string attachmentId)
    {
        var separator = attachmentId.IndexOf('/');
        if (separator <= 0 || separator == attachmentId.Length - 1)
        {
            return null;
        }

        var last = attachmentId[(separator + 1)..];
        var dot = last.LastIndexOf('.');
        return dot < 0
            ? (attachmentId[..separator], last)
            : dot == 0 || dot == last.Length - 1
                ? null
                : (last[..dot], last[(dot + 1)..]);
    }
}