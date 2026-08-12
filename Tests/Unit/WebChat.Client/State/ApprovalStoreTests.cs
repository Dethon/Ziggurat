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
}