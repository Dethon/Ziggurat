using Domain.Contracts;
using Domain.Security;
using Domain.Tools.HomeAssistant.Vfs;

namespace McpServerHomeAssistant.Services;

// The watches' delegations as the agent's operator commands reach them. A watch records no account
// as its writer: one written before delegations fired for nobody, and still does until a person's
// turn writes it again.
public sealed class WatchDelegationStore(HaWatches watches) : IDelegationStore
{
    public async Task<IReadOnlyList<StoredDelegation>> ListAsync(CancellationToken ct) =>
    [
        .. (await watches.ListAsync(ct)).Select(w => new StoredDelegation(
            DelegatedObject.Watch, w.Id, w.Meta.Delegation, WatchDelegation.Of(w).Description, null))
    ];

    public async Task<bool> ReplaceAsync(string kind, string id, string token, CancellationToken ct)
    {
        if (kind != DelegatedObject.Watch || await watches.GetAsync(id, ct) is not { } watch)
        {
            return false;
        }

        await watches.ReplaceDelegationAsync(watch, token, ct);
        return true;
    }
}