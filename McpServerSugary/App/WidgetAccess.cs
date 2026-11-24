using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Domain.Contracts;
using Domain.DTOs.Channel;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.App;

// Who a widget's address reads for. The proxy's session check is not in front of /widget, so this stands
// in for it on every request: the token finds its row, the accounts service vouches afresh for the
// session that made it, and SugaryAccess decides as it does for the app. The log read is the one the row
// names, never one the address names, so nothing added to the address widens it.
//
// A session that ended ends the token for good. An outage shows nothing and keeps it, and a suspended
// household reads nothing until it is resumed: neither is the person signing out.
internal static class WidgetAccess
{
    public const string Suspended = "household-suspended";
    public const string Unavailable = "accounts-unavailable";

    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string? TokenIn(PathString afterWidget) =>
        afterWidget.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var token, ..] ? token : null;

    public static async Task<IResult?> AdmitAsync(HttpContext http, string? token, CancellationToken ct)
    {
        if (token is null)
        {
            return Results.NotFound();
        }

        var services = http.RequestServices;
        var hash = Hash(token);
        await using var db = await services.GetRequiredService<IDbContextFactory<SugaryDbContext>>().CreateDbContextAsync(ct);
        var row = await db.WidgetTokens.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Hash == hash, ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        ResolvedAccount? session;
        try
        {
            session = await services.GetRequiredService<ISessionAccounts>().SessionAsync(row.UserId, row.SessionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WidgetAccess))
                .LogWarning(ex, "Could not ask whether the session behind a widget of {UserId} lives", row.UserId);
            return Refuse(StatusCodes.Status503ServiceUnavailable, Unavailable, "Try again in a moment.");
        }

        if (session is null || session.Account != new Account(row.TenantId, row.UserId))
        {
            await db.WidgetTokens.IgnoreQueryFilters().Where(t => t.Hash == hash).ExecuteDeleteAsync(ct);
            return Results.NotFound();
        }

        if (session.HouseholdSuspended)
        {
            return Refuse(StatusCodes.Status403Forbidden, Suspended, "This household is suspended.");
        }

        var person = new Person(session.Account, session.Assertion) { SessionId = row.SessionId };
        return await SugaryAccess.AdmitAsync(http, person, row.LogUserId, ct);
    }

    private static IResult Refuse(int status, string code, string detail) =>
        Results.Problem(detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}