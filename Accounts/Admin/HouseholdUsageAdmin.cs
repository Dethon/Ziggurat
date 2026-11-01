using System.Globalization;
using Accounts.Members;
using Domain.DTOs.Admin;
using Domain.DTOs.Metrics;

namespace Accounts.Admin;

// What the caller's household used over a calendar month, for its owners: the Observability host's
// figures (HouseholdUsageClient), asked for the owner the gate verified, so the household is the
// caller's and no route here names another. A read, so nothing is recorded.
//
// A person's line is keyed by their account and shown under the name the household knows them by
// now, so a rename since shows the new name over the whole month; someone no longer a member keeps
// the name their calls carried.
//
// A month is the deployment's days from its first to its last, in UTC, because the metrics are kept
// by UTC day; the current one, when none is named, is this service's clock's.
public static class HouseholdUsageAdmin
{
    public const string UnavailableMessage = "The household's usage could not be read just now. Try again in a moment.";

    public static void Map(RouteGroupBuilder household) =>
        household.MapGet(AdminApi.Under(AdminProtocol.Household, AdminProtocol.Usage), ReadAsync);

    private static async Task<IResult> ReadAsync(
        AdminCaller caller, string? month, HouseholdUsageClient usage, MemberStore members, AccountsClock clock,
        CancellationToken ct)
    {
        var first = month is null
            ? FirstOf(DateOnly.FromDateTime(clock.Time.GetUtcNow().UtcDateTime))
            : DateOnly.TryParseExact(month, AdminUsage.MonthFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var named)
                ? named
                : (DateOnly?)null;
        if (first is not { } from)
        {
            return Results.BadRequest(new AdminRefusal($"A month is written {AdminUsage.MonthFormat}, such as 2026-10.", []));
        }

        if (await usage.ReadAsync(caller.Person, from, from.AddMonths(1).AddDays(-1), ct) is not { } read)
        {
            return Results.Json(new AdminRefusal(UnavailableMessage, []), statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var names = (await members.ListAsync(caller.HouseholdId, ct)).ToDictionary(m => m.Id, m => m.DisplayName);
        return Results.Ok(new AdminUsage(
            from.ToString(AdminUsage.MonthFormat, CultureInfo.InvariantCulture), read.Agents,
            [.. read.People.Select(line => line.Id is { } id && names.TryGetValue(id, out var name) ? line with { Name = name } : line)],
            read.Lemonade));
    }

    private static DateOnly FirstOf(DateOnly day) => new(day.Year, day.Month, 1);
}