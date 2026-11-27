namespace Domain.DTOs;

public enum ReplyContentType
{
    Text,
    Reasoning,
    Error,
    StreamComplete,

    // A tool call as the model wrote it, and its answer once it ran: a ToolCallStep as JSON
    // (ChannelProtocol.SerializerOptions). Sent only to a channel that shows them
    // (IChannelConnection.ShowsToolCalls), since a server from before these cannot read them.
    ToolCall,
    ToolResult
}