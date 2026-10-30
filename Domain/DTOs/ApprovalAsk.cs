using Domain.DTOs.Channel;

namespace Domain.DTOs;

// One question put to a person about the tool calls a turn wants to make. The id is the question's
// own wherever it is shown, so every channel that did not answer it can be told who did. The turn is
// whose question it is: only the account it acts for may answer, on any channel, and a turn acting
// for none — the voice guest's, a fire nobody stamped — is answered by each channel's own rule.
public record ApprovalAsk(
    string ApprovalId,
    string ConversationId,
    IReadOnlyList<ToolApprovalRequest> Requests,
    ConversationContext? Turn)
{
    public Account? Asker => Principal.Of(Turn)?.Account;

    public static ApprovalAsk For(
        string conversationId, IReadOnlyList<ToolApprovalRequest> requests, ConversationContext? turn) =>
        new(Guid.NewGuid().ToString("N"), conversationId, requests, turn);
}