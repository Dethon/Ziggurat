using System.Collections.Immutable;
using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Approval;

// TopicId is the topic the question was asked in, or null for one another channel asked and the
// hub offered to this person wherever they are. AnsweredElsewhere: answered on another of its
// asker's channels, so the card stays to say so until the person closes it.
public sealed record PendingApproval(
    string? TopicId, ToolApprovalRequestMessage Request, bool AnsweredElsewhere = false);

public sealed record ApprovalState
{
    // Every request still waiting for an answer, oldest first. A single slot let a second
    // conversation's request hide the first one with no way to bring it back.
    public ImmutableList<PendingApproval> Pending { get; init; } = [];

    // The prompt on screen is the oldest one still waiting; answering it surfaces the next.
    public ToolApprovalRequestMessage? CurrentRequest => Pending.FirstOrDefault()?.Request;

    public string? TopicId => Pending.FirstOrDefault()?.TopicId;

    public static ApprovalState Initial => new();
}

public enum ApprovalCardStatus
{
    Answerable,
    // Someone else's question: shown to whoever sees it, answered by its asker alone.
    NotYours,
    AnsweredElsewhere
}

// The card on screen as the person looking at it sees it.
public sealed record ApprovalCard(ToolApprovalRequestMessage Request, ApprovalCardStatus Status)
{
    // A question acting for nobody is answered by its topic's own rule, which the hub keeps.
    public static ApprovalCard? Of(ApprovalState state, string? viewerId) =>
        state.Pending.FirstOrDefault() is not { } shown
            ? null
            : new ApprovalCard(shown.Request, shown switch
            {
                { AnsweredElsewhere: true } => ApprovalCardStatus.AnsweredElsewhere,
                { Request.AskedBy: { } asker } when asker != viewerId => ApprovalCardStatus.NotYours,
                _ => ApprovalCardStatus.Answerable
            });
}