using System.Globalization;
using Domain.DTOs.Admin;

namespace Accounts.Admin;

// What the caller's household used over a calendar month, for its owners: the Observability host's
// figures (HouseholdUsageClient), asked for the owner the gate verified, so the household is the
// caller's and no route here names another. A read, so nothing is recorded.
//
// A month is the deployment's days from its first to its last, in UTC, because the metrics are kept
// by UTC day; the current one, when none is named, is this service's clock's.
public static class HouseholdUsageAdmin
{
    public const string UnavailableMessage = "The household's usage could not be read just now. Try again in a moment.";

    public static void Map(RouteGroupBuilder household) =>
        household.MapGet(AdminApi.Under(AdminProtocol.Household, AdminProtocol.Usage), ReadAsync);

    private static async Task<IResult> ReadAsync(
        AdminCaller caller, string? month, HouseholdUsageClient usage, AccountsClock clock, CancellationToken ct)
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

        return await usage.ReadAsync(caller.Person, from, from.AddMonths(1).AddDays(-1), ct) is { } read
            ? Results.Ok(new AdminUsage(
                from.ToString(AdminUsage.MonthFormat, CultureInfo.InvariantCulture), read.Agents, read.People, read.Lemonade))
            : Results.Json(new AdminRefusal(UnavailableMessage, []), statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static DateOnly FirstOf(DateOnly day) => new(day.Year, day.Month, 1);
}