using Accounts.Store;
using Domain.DTOs.Admin;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Admin;

// Every change made through the admin API, so a change nobody remembers making can be traced: who
// (the account the admin assertion named), what (the route and a summary the route writes), on which
// household, and when. A route records once its change has stood — a refused change changed nothing
// and is not recorded — and its summary names what changed, never a secret or any part of one.
//
// Read back by the household's owners (their own household's) and by the operator (every one's).
public sealed class AdminLog(IDbContextFactory<AccountsDbContext> contexts, AccountsClock clock)
{
    public const int RouteLength = 300;
    public const int SummaryLength = 500;

    // A change on a household route, which is the caller's own household.
    public Task RecordAsync(HttpContext http, AdminCaller caller, string summary, CancellationToken ct) =>
        RecordAsync(http, caller, caller.HouseholdId, summary, ct);

    // A change on an operator route, which names the household it changed.
    public async Task RecordAsync(HttpContext http, AdminCaller caller, string householdId, string summary, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.AdminChanges.Add(new AdminChange
        {
            Id = Tokens.NewId(),
            TenantId = householdId,
            ActorUserId = caller.UserId,
            ActorTenantId = caller.Person.TenantId,
            Route = Cut($"{http.Request.Method} {http.Request.Path}", RouteLength),
            Summary = Cut(summary, SummaryLength),
            At = clock.Time.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
    }

    // Newest first; every household's when none is named.
    public async Task<IReadOnlyList<AdminChangeEntry>> ListAsync(string? householdId, int? limit, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var changes = await db.AdminChanges.AsNoTracking()
            .Where(c => householdId == null || c.TenantId == householdId)
            .OrderByDescending(c => c.At)
            .ThenByDescending(c => c.Id)
            .Take(Math.Clamp(limit ?? AdminProtocol.DefaultChangeCount, 1, AdminProtocol.MaxChangeCount))
            .ToListAsync(ct);
        var actors = changes.Select(c => c.ActorUserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking()
            .Where(u => actors.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return
        [
            .. changes.Select(c => new AdminChangeEntry(
                c.Id, c.TenantId, c.ActorUserId, c.ActorTenantId, names.GetValueOrDefault(c.ActorUserId, ""), c.Route,
                c.Summary, c.At))
        ];
    }

    public static void Map(RouteGroupBuilder household, RouteGroupBuilder deployment)
    {
        household.MapGet(AdminApi.Under(AdminProtocol.Household, AdminProtocol.HouseholdChanges),
            async (AdminCaller caller, int? limit, AdminLog log, CancellationToken ct) =>
                Results.Ok(await log.ListAsync(caller.HouseholdId, limit, ct)));

        deployment.MapGet(AdminApi.Under(AdminProtocol.Operator, AdminProtocol.AllChanges),
            async (string? household, int? limit, AdminLog log, CancellationToken ct) =>
                Results.Ok(await log.ListAsync(household, limit, ct)));
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];
}