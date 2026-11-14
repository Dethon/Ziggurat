using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Effects;

namespace Tests.Unit.WebChat.Client.State;

// A question another channel asked is offered to the chat clients open at that moment, so one
// opened afterwards — from the push that told its person — asks the hub what it still offers.
public sealed class OfferedApprovalsEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly ConnectionStore _connectionStore;
    private readonly ApprovalStore _approvalStore;
    private readonly FakeApprovalService _approvals = new();
    private readonly OfferedApprovalsEffect _effect;

    public OfferedApprovalsEffectTests()
    {
        _connectionStore = new ConnectionStore(_dispatcher);
        _approvalStore = new ApprovalStore(_dispatcher);
        _effect = new OfferedApprovalsEffect(
            _connectionStore, _approvals, _dispatcher, new RecordingLogger<OfferedApprovalsEffect>());
    }

    public void Dispose()
    {
        _effect.Dispose();
        _approvalStore.Dispose();
        _connectionStore.Dispose();
    }

    [Fact]
    public async Task BecomingLive_TheFirstTimeAndAfterAnInterruption_ShowsWhatIsStillOffered()
    {
        _approvals.Offered.Add(new ToolApprovalRequestMessage("offer-1", [], "fran"));

        _dispatcher.Dispatch(new ConnectionConnected());

        await Eventually.Until(() => _approvalStore.State.CurrentRequest?.ApprovalId == "offer-1", "the offer to show");
        _approvalStore.State.TopicId.ShouldBeNull();

        _approvals.Offered.Clear();
        _approvals.Offered.Add(new ToolApprovalRequestMessage("offer-2", [], "fran"));
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnected());

        await Eventually.Until(() => _approvalStore.State.CurrentRequest?.ApprovalId == "offer-2", "the catch-up to replace it");
        _approvalStore.State.Pending.ShouldHaveSingleItem();
    }
}