using Accounts.Links;
using Accounts.People;
using Accounts.Store;
using Accounts.Tunnels;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Admin;

// The operator's household list in the dashboard: every household with its people, the servers
// connected to the agent's relay now and when its people were last seen; a household created with
// its owner and the owner's passkey link; an owner's recovery link. Each link is shown once, in the
// answer that minted it, and never in the change log.
//
// Last seen is the session check's: the latest any of the household's browser sessions passed it
// (SessionStore writes it at a five-minute grain). A person who only uses Telegram or voice never
// moves it; what a household spends is the usage view's, read from the metrics.
public static class HouseholdListAdmin
{
    public static void Map(RouteGroupBuilder deployment)
    {
        deployment.MapGet(AdminApi.Under(AdminProtocol.Operator, AdminProtocol.Households), ListAsync);
        deployment.MapPost(AdminApi.Under(AdminProtocol.Operator, AdminProtocol.Households), CreateAsync);
        deployment.MapPost(AdminApi.Under(AdminProtocol.Operator, AdminProtocol.OwnerRecoveryRoute), RecoverOwnerAsync);
    }

    private static async Task<IResult> ListAsync(
        IDbContextFactory<AccountsDbContext> contexts, ConnectedServersClient relay, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var households = await db.Tenants.AsNoTracking()
            .OrderByDescending(t => t.IsHome)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .ToListAsync(ct);
        var people = (await db.Users.AsNoTracking().OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).ToListAsync(ct))
            .ToLookup(u => u.TenantId);
        var lastSeen = await db.Sessions.AsNoTracking()
            .GroupBy(s => s.TenantId)
            .Select(g => new { TenantId = g.Key, LastSeenAt = g.Max(s => s.LastSeenAt) })
            .ToDictionaryAsync(s => s.TenantId, s => s.LastSeenAt, ct);
        var connected = await Task.WhenAll(households.Select(h => relay.ListAsync(h.Id, ct)));

        return Results.Ok(households.Zip(connected, (household, servers) => new HouseholdListEntry(
            household.Id,
            household.Name,
            household.IsHome,
            household.CreatedAt,
            [.. people[household.Id].Where(u => u.Role == Role.Owner).Select(Person)],
            [.. people[household.Id].Where(u => u.Role != Role.Owner).Select(Person)],
            servers?.Select(s => new HouseholdConnectedServer(s.ServerName, s.OpenedAt)).ToList(),
            lastSeen.TryGetValue(household.Id, out var seen) ? seen : null)).ToList());
    }

    private static async Task<IResult> CreateAsync(
        AdminCaller caller, NewHousehold? request, HttpContext http, LinkStore links, AdminLog log, CancellationToken ct)
    {
        if (request is null)
        {
            return Results.BadRequest(new AdminRefusal("The household was not created: send it as a JSON object.", []));
        }

        var name = request.Name?.Trim() ?? "";
        var (owner, ownerProblem) = Profile.Read(request.OwnerName, avatarUrl: null);
        var problems = new[]
        {
            name.Length is 0 or > HouseholdSettings.NameLength
                ? $"The household's name must be 1–{HouseholdSettings.NameLength} characters."
                : null,
            ownerProblem is null ? null : $"The owner's name: {ownerProblem}"
        }.OfType<string>().ToList();
        if (problems.Count > 0)
        {
            return Results.BadRequest(new AdminRefusal("The household was not created.", problems));
        }

        var minted = await links.CreateHouseholdAsync(name, owner!.DisplayName, caller.UserId, ct);
        await log.RecordAsync(http, caller, minted.TenantId, "Created the household and its owner, and minted the owner's link", ct);
        return Results.Ok(new AdminSaved<OwnerLink>(LinkOf(minted)));
    }

    // Any household's owner, so a household whose only owner lost every passkey is not locked out.
    // Not a member — their owners mint theirs — and not the operator, whose way back is their own
    // account page or the container command: a stolen dashboard session must not become a passkey.
    private static async Task<IResult> RecoverOwnerAsync(
        AdminCaller caller, string householdId, string userId, HttpContext http,
        IDbContextFactory<AccountsDbContext> contexts, LinkStore links, AdminLog log, CancellationToken ct)
    {
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            var person = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == householdId, ct);
            switch (person)
            {
                case null:
                    return Results.NotFound(new AdminRefusal("That household has nobody by that id.", []));
                case { IsOperator: true }:
                    return Results.BadRequest(new AdminRefusal(
                        "The operator adds a passkey from their own account page, or with the container command.", []));
                case { Role: not Role.Owner }:
                    return Results.BadRequest(new AdminRefusal(
                        "A member's recovery link is minted by their household's owners.", []));
            }
        }

        if (await links.MintRecoveryAsync(householdId, caller.UserId, userId, ct) is not { } minted)
        {
            return Results.NotFound(new AdminRefusal("That household has nobody by that id.", []));
        }

        await log.RecordAsync(http, caller, householdId, $"Minted a recovery link for the owner {userId}", ct);
        return Results.Ok(new AdminSaved<OwnerLink>(LinkOf(minted)));
    }

    private static HouseholdPerson Person(User user) => new(user.Id, user.DisplayName, user.IsOperator);

    private static OwnerLink LinkOf(MintedLink minted) =>
        new(minted.TenantId, minted.UserId, minted.DisplayName, minted.Url.ToString(), minted.ExpiresAt);
}