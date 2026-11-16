using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Approval;

namespace Tests.Unit.WebChat.Client.State;

public sealed class ApprovalStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly ApprovalStore _store;

    public ApprovalStoreTests()
    {
        _store = new ApprovalStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    private static ToolApprovalRequestMessage Approval(string approvalId) => new(approvalId, []);

    // Two conversations can be waiting on the agent at once. A second request must queue behind
    // the first rather than replace it, or the first prompt is gone with no way to answer it.
    [Fact]
    public void ShowApproval_ASecondConversationAlsoAsks_TheFirstPromptStays()
    {
        _dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1")));
        _dispatcher.Dispatch(new ShowApproval("topic-2", Approval("approval-2")));

        _store.State.CurrentRequest?.ApprovalId.ShouldBe("approval-1");
        _store.State.TopicId.ShouldBe("topic-1");
    }

    [Fact]
    public void ShowApproval_TheSameRequestArrivesTwice_IsPendingOnce()
    {
        _dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1")));
        _dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1")));
        _dispatcher.Dispatch(new ApprovalResolved("approval-1"));

        _store.State.CurrentRequest.ShouldBeNull();
    }

    [Fact]
    public void ApprovalResolved_TheCurrentOne_SurfacesTheNextPending()
    {
        _dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1")));
        _dispatcher.Dispatch(new ShowApproval("topic-2", Approval("approval-2")));

        _dispatcher.Dispatch(new ApprovalResolved("approval-1"));

        _store.State.CurrentRequest?.ApprovalId.ShouldBe("approval-2");
        _store.State.TopicId.ShouldBe("topic-2");
    }

    // A request resolved while it was queued behind another one takes only itself away. The
    // prompt on screen belongs to a different conversation and is still waiting for an answer.
    [Fact]
    public void ApprovalResolved_OneQueuedBehind_LeavesThePromptOnScreen()
    {
        _dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1")));
        _dispatcher.Dispatch(new ShowApproval("topic-2", Approval("approval-2")));

        _dispatcher.Dispatch(new ApprovalResolved("approval-2"));

        _store.State.CurrentRequest?.ApprovalId.ShouldBe("approval-1");
    }

    [Fact]
    public void ClearApproval_TheAnsweredOne_SurfacesTheNextPending()
    {
        _dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1")));
        _dispatcher.Dispatch(new ShowApproval("topic-2", Approval("approval-2")));

        _dispatcher.Dispatch(new ClearApproval("approval-1"));

        _store.State.CurrentRequest?.ApprovalId.ShouldBe("approval-2");
    }

    // What the server says is pending for a conversation is the whole truth about it: an
    // approval it no longer lists was resolved or timed out while this client was away.
    [Fact]
    public void TopicApprovalsReconciled_TheServerListsNothing_DropsThatTopicsPrompts()
    {
        _dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1")));
        _dispatcher.Dispatch(new ShowApproval("topic-2", Approval("approval-2")));

        _dispatcher.Dispatch(new TopicApprovalsReconciled("topic-1", null));

        _store.State.CurrentRequest?.ApprovalId.ShouldBe("approval-2");
    }

    [Fact]
    public void TopicApprovalsReconciled_TheServerListsAnother_ReplacesTheStaleOne()
    {
        _dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1")));

        _dispatcher.Dispatch(new TopicApprovalsReconciled("topic-1", Approval("approval-3")));

        _store.State.CurrentRequest?.ApprovalId.ShouldBe("approval-3");
        _store.State.TopicId.ShouldBe("topic-1");
    }

    [Fact]
    public void TopicApprovalsReconciled_TheServerListsTheSameOne_KeepsItWhereItWas()
    {
        _dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1")));
        _dispatcher.Dispatch(new ShowApproval("topic-2", Approval("approval-2")));

        _dispatcher.Dispatch(new TopicApprovalsReconciled("topic-2", Approval("approval-2")));

        _store.State.CurrentRequest?.ApprovalId.ShouldBe("approval-1");
        _dispatcher.Dispatch(new ApprovalResolved("approval-1"));
        _store.State.CurrentRequest?.ApprovalId.ShouldBe("approval-2");
    }

    // Answered on another of its asker's channels, the card stays to say so rather than vanishing
    // under the person's finger, and is theirs to close.
    [Fact]
    public void ApprovalResolved_AnsweredElsewhere_KeepsTheCardToSaySo_UntilItIsClosed()
    {
        _dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1")));

        _dispatcher.Dispatch(new ApprovalResolved("approval-1", AnsweredElsewhere: true));

        ApprovalCard.Of(_store.State, "fran")
            .ShouldBe(new ApprovalCard(Approval("approval-1"), ApprovalCardStatus.AnsweredElsewhere));
        _dispatcher.Dispatch(new ClearApproval("approval-1"));
        _store.State.CurrentRequest.ShouldBeNull();
    }

    // A question is its asker's: whoever sees someone else's sees it without the buttons to answer.
    [Fact]
    public void ApprovalCard_AnotherPersonsQuestion_IsShownButNotAnswerable()
    {
        _dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1") with { AskedBy = "laura" }));

        ApprovalCard.Of(_store.State, "fran")!.Status.ShouldBe(ApprovalCardStatus.NotYours);
        ApprovalCard.Of(_store.State, "laura")!.Status.ShouldBe(ApprovalCardStatus.Answerable);
    }

    // A question acting for nobody is answered by the topic's own rule, which the hub keeps.
    [Fact]
    public void ApprovalCard_AQuestionActingForNobody_IsAnswerable()
    {
        _dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1")));

        ApprovalCard.Of(_store.State, "fran")!.Status.ShouldBe(ApprovalCardStatus.Answerable);
    }

    // What the server still offers this person is the whole truth about offers: one it no longer
    // lists was answered or called off while the client was away, and one it lists is shown.
    [Fact]
    public void OfferedApprovalsReconciled_ReplacesTheOffersAndLeavesTopicsPromptsAlone()
    {
        _dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1")));
        _dispatcher.Dispatch(new ShowApproval(null, Approval("offer-1")));

        _dispatcher.Dispatch(new OfferedApprovalsReconciled([Approval("offer-2")]));

        _store.State.Pending.Select(p => p.Request.ApprovalId).ShouldBe(["approval-1", "offer-2"]);
        _dispatcher.Dispatch(new TopicApprovalsReconciled("topic-1", null));
        _store.State.CurrentRequest?.ApprovalId.ShouldBe("offer-2");
    }

    // A card that says it was answered elsewhere is the person's to close, so catching up keeps it.
    [Fact]
    public void OfferedApprovalsReconciled_KeepsACardSayingItWasAnsweredElsewhere()
    {
        _dispatcher.Dispatch(new ShowApproval(null, Approval("offer-1")));
        _dispatcher.Dispatch(new ApprovalResolved("offer-1", AnsweredElsewhere: true));

        _dispatcher.Dispatch(new OfferedApprovalsReconciled([]));

        _store.State.CurrentRequest?.ApprovalId.ShouldBe("offer-1");
    }
}