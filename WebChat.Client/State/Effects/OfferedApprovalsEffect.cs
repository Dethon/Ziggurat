using System.Reactive.Linq;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Connection;

namespace WebChat.Client.State.Effects;

// A question another channel asked is offered to its person on the chat clients they have open at
// that moment; one opened afterwards — from the push that told them, say — or one that was between
// connections asks the hub what it still offers, every time it becomes live, the first time too.
public sealed class OfferedApprovalsEffect : IDisposable
{
    private readonly IDisposable _subscription;

    public OfferedApprovalsEffect(
        ConnectionStore connectionStore,
        IApprovalService approvalService,
        Dispatcher dispatcher,
        ILogger<OfferedApprovalsEffect> logger)
    {
        _subscription = connectionStore.StateObservable
            .Where(state => state.Status == ConnectionStatus.Connected)
            .Select(state => state.Epoch)
            .DistinctUntilChanged()
            .Subscribe(_ => CatchUpAsync(approvalService, dispatcher).LogFaults(logger, "offered approvals"));
    }

    private static async Task CatchUpAsync(IApprovalService approvalService, Dispatcher dispatcher)
    {
        var offered = await approvalService.GetOfferedApprovalsAsync();
        if (offered.IsLive)
        {
            dispatcher.Dispatch(new OfferedApprovalsReconciled(offered.Value ?? []));
        }
    }

    public void Dispose() => _subscription.Dispose();
}