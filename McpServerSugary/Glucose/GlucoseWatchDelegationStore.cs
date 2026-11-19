using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Tools.Sugary.Watches;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.Glucose;

// The glucose watches' delegations as the agent's operator commands reach them (redelegate,
// retire-signing-keys). The operator's housekeeping reaches every household's watches, so the factory's
// context is used, IgnoreQueryFilters said, and a delegation names its watch by id alone. A watch with
// none is offered as the person who set it up, its recorded writer — a label the operator confirms, never
// evidence.
public sealed class GlucoseWatchDelegationStore(IDbContextFactory<SugaryDbContext> contexts) : IDelegationStore
{
    public async Task<IReadOnlyList<StoredDelegation>> ListAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var watches = await db.GlucoseWatches.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        return
        [
            .. watches.Select(w => new StoredDelegation(
                DelegatedObject.Watch, w.Id, w.Delegation, GlucoseWatchDelegation.Of(SqliteGlucoseWatches.ToStored(w)).Description,
                new Principal(ChannelIds.Sugary, w.WriterUserId, new Account(w.TenantId, w.WriterUserId))))
        ];
    }

    // Only the delegation is rewritten: the watch, what was judged of it and whether it is spent stay as
    // they are. An id is unique within its household only, and the token is not this server's to read, so
    // an id two households both hold is refused rather than guessed at: replacing the wrong one would make
    // it fire as somebody of another household.
    public async Task<bool> ReplaceAsync(string kind, string id, string token, CancellationToken ct)
    {
        if (kind != DelegatedObject.Watch)
        {
            return false;
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        var holders = await db.GlucoseWatches.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => w.TenantId)
            .ToListAsync(ct);
        if (holders is not [var household])
        {
            return false;
        }

        return await db.GlucoseWatches.IgnoreQueryFilters()
            .Where(w => w.TenantId == household && w.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.Delegation, token), ct) == 1;
    }
}