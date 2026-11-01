using Accounts.Announcements;
using Accounts.Store;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Domain.DTOs.Tunnel;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Admin;

// The operator stops a household without deleting anything, and starts it again. The household's row
// says so (Tenant.SuspendedAt), and each place that lets the household in reads it there: the session
// check refuses its sessions, the agent's view of it says suspended so its turns and fires are refused,
// and its tunnel credentials open nothing. Two announcements make it take hold at once rather than when
// something next looks: the readers of its configuration drop what they kept, and the relay closes its
// open tunnels.
public static class HouseholdSuspensionAdmin
{
    // A suspension the listeners were not told of still stands: what is open of the household may run on
    // until the agent next reads it (a minute) or its tunnels next open.
    public const string NotToldWarning =
        "Done, but the agent could not be told just now: the household's open tunnels and conversations may run for up to a minute more.";

    public static void Map(RouteGroupBuilder deployment) =>
        deployment.MapPut(AdminApi.Under(AdminProtocol.Operator, AdminProtocol.SuspensionRoute), SetAsync);

    private static async Task<IResult> SetAsync(
        AdminCaller caller, string householdId, HouseholdSuspension? request, HttpContext http,
        IDbContextFactory<AccountsDbContext> contexts, AccountsClock clock, RedisAnnouncer announcer, AdminLog log,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Results.BadRequest(new AdminRefusal("Nothing was changed: send the suspension as a JSON object.", []));
        }

        DateTimeOffset? since;
        bool changed;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            switch (await db.Tenants.FirstOrDefaultAsync(t => t.Id == householdId, ct))
            {
                case null:
                    return Results.NotFound(new AdminRefusal("There is no household by that id.", []));
                // The operator's own: suspending it would lock them out of the dashboard that resumes it.
                case { IsHome: true }:
                    return Results.BadRequest(new AdminRefusal("The home household is the operator's own and cannot be suspended.", []));
                case var household:
                    changed = request.Suspended != (household.SuspendedAt is not null);
                    household.SuspendedAt = request.Suspended ? household.SuspendedAt ?? clock.Time.GetUtcNow() : null;
                    since = household.SuspendedAt;
                    await db.SaveChangesAsync(ct);
                    break;
            }
        }

        // Told again when nothing changed, so asking twice is the retry when they could not be told.
        var notTold = await TellAsync(announcer, householdId, request.Suspended, ct);
        if (changed)
        {
            await log.RecordAsync(http, caller, householdId,
                request.Suspended ? "Suspended the household" : "Resumed the household", ct);
        }

        return Results.Ok(new AdminSaved<HouseholdSuspension>(
            new HouseholdSuspension(request.Suspended, since), notTold is null ? null : NotToldWarning));
    }

    // Resuming tells the readers alone: the household's servers reconnect by themselves, and are let in
    // because their credentials verify again.
    private static async Task<string?> TellAsync(RedisAnnouncer announcer, string householdId, bool suspended, CancellationToken ct)
    {
        var readers = await announcer.AnnounceAsync(HouseholdConfigProtocol.SavedChannel,
            new HouseholdConfigSaved(householdId, HouseholdConfigProtocol.SuspensionSection), ct);
        var relay = suspended
            ? await announcer.AnnounceAsync(TunnelCredentialProtocol.SuspendedChannel, new HouseholdSuspended(householdId), ct)
            : null;
        return readers ?? relay;
    }
}