using System.Collections.Immutable;
using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Approval;

// A null topic is a question another channel asked, offered to this person wherever they are.
public record ShowApproval(string? TopicId, ToolApprovalRequestMessage Request) : IAction;

// Answered elsewhere keeps the card to say so; any other resolution takes it away.
public record ApprovalResolved(string ApprovalId, bool AnsweredElsewhere = false) : IAction;

public record ClearApproval(string ApprovalId) : IAction;

// What the server says is still pending for one conversation, which is the whole truth about
// it: anything else this client holds for that conversation was resolved or timed out while it
// was away, and a deleted conversation leaves nothing pending at all.
public record TopicApprovalsReconciled(string TopicId, ToolApprovalRequestMessage? StillPending) : IAction;

// What the hub still offers this person, which is the whole truth about offers in the same way.
public record OfferedApprovalsReconciled(IReadOnlyList<ToolApprovalRequestMessage> StillOffered) : IAction;

public sealed class ApprovalStore : IDisposable
{
    private readonly Store<ApprovalState> _store;

    public ApprovalStore(Dispatcher dispatcher)
    {
        _store = new Store<ApprovalState>(ApprovalState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public ApprovalState State => _store.State;

    public IObservable<ApprovalState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static ApprovalState Reduce(ApprovalState state, IAction action) => action switch
    {
        ShowApproval show => state with { Pending = Queue(state.Pending, show.TopicId, show.Request) },
        ApprovalResolved { AnsweredElsewhere: true } resolved => state with
        {
            Pending = state.Pending.Select(p => p.Request.ApprovalId == resolved.ApprovalId
                ? p with { AnsweredElsewhere = true }
                : p).ToImmutableList()
        },
        ApprovalResolved resolved => state with { Pending = Drop(state.Pending, resolved.ApprovalId) },
        ClearApproval clear => state with { Pending = Drop(state.Pending, clear.ApprovalId) },
        TopicApprovalsReconciled reconciled => state with { Pending = Reconcile(state.Pending, reconciled) },
        OfferedApprovalsReconciled offered => state with { Pending = ReconcileOffers(state.Pending, offered.StillOffered) },
        _ => state
    };

    private static ImmutableList<PendingApproval> Queue(
        ImmutableList<PendingApproval> pending, string? topicId, ToolApprovalRequestMessage request) =>
        pending.Any(p => p.Request.ApprovalId == request.ApprovalId)
            ? pending
            : pending.Add(new PendingApproval(topicId, request));

    private static ImmutableList<PendingApproval> Drop(
        ImmutableList<PendingApproval> pending, string approvalId) =>
        pending.RemoveAll(p => p.Request.ApprovalId == approvalId);

    private static ImmutableList<PendingApproval> Reconcile(
        ImmutableList<PendingApproval> pending, TopicApprovalsReconciled reconciled)
    {
        var kept = pending.RemoveAll(p =>
            p.TopicId == reconciled.TopicId &&
            !p.AnsweredElsewhere &&
            p.Request.ApprovalId != reconciled.StillPending?.ApprovalId);

        return reconciled.StillPending is null
            ? kept
            : Queue(kept, reconciled.TopicId, reconciled.StillPending);
    }

    // A card saying it was answered elsewhere is the person's to close, so it is kept.
    private static ImmutableList<PendingApproval> ReconcileOffers(
        ImmutableList<PendingApproval> pending, IReadOnlyList<ToolApprovalRequestMessage> stillOffered)
    {
        var offered = stillOffered.Select(r => r.ApprovalId).ToHashSet();
        var kept = pending.RemoveAll(p =>
            p.TopicId is null && !p.AnsweredElsewhere && !offered.Contains(p.Request.ApprovalId));
        return stillOffered.Aggregate(kept, (queue, request) => Queue(queue, null, request));
    }
}