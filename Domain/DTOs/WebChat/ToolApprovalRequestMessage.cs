namespace Domain.DTOs.WebChat;

// AskedBy is the account whose question this is, the only one who may answer it; null for a
// question acting for nobody, which the topic's own rule answers. The client shows the buttons by
// it, and the hub holds an answer to it whatever the client shows.
public record ToolApprovalRequestMessage(
    string ApprovalId,
    IReadOnlyList<ToolApprovalRequest> Requests,
    string? AskedBy = null);