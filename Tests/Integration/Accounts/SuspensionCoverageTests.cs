using Microsoft.AspNetCore.Routing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// Every way into the accounts service says what it does for a suspended household (.claude/rules/
// admin.md, "Suspension"), because each one checks for itself — the suspension is one column, and the
// ways in read it in queries of their own shapes. So a route added here fails this test until it is
// placed below, by path, with how it refuses a suspended household or why it need not; the behaviour
// itself is each entry's own suite's (named beside it). Memory extraction was the way in on the agent's
// side that forgot, which is the mistake this list keeps from happening here.
public sealed class SuspensionCoverageTests
{
    // Longest prefix wins. A reason is a sentence a reader of the route can check.
    private static readonly Dictionary<string, string> _coverage = new(StringComparer.Ordinal)
    {
        ["/accounts/verify"] =
            "refuses: the session check answers a suspended household's session 403 on every channel (SessionCheck.Suspended)",
        ["/accounts/me"] =
            "exempt: names the person and nothing else, deliberately (admin.md, 'Not stopped by it')",
        ["/accounts/account"] = "refuses: PageGuard answers SessionCheck.Suspended before anything else",
        ["/accounts/members"] = "exempt: a redirect to the admin area's members page, whose API the session check refuses while suspended",
        ["/accounts/api/passkeys"] = "refuses: PageGuard answers SessionCheck.Suspended before the ceremony",
        ["/accounts/api/sign-in"] =
            "exempt: a sign-in makes a session and nothing else; every way in refuses that session while suspended",
        ["/accounts/enroll"] =
            "exempt: an enrollment adds a passkey and a session that every way in refuses while suspended",
        ["/accounts/api/enroll"] =
            "exempt: an enrollment adds a passkey and a session that every way in refuses while suspended",
        ["/accounts/sign-in"] = "exempt: the sign-in page, signed out by definition",
        ["/accounts/passkeys.js"] = "exempt: the pages' script, no household's",
        ["/accounts/theme.js"] = "exempt: the pages' theme script, no household's",
        ["/accounts/sign-out"] =
            "exempt: ends the caller's own session and nothing else, which a suspended household's person may still do (AccountPageTests.ASuspendedHouseholdsPerson_CanStillSignOut)",
        ["/accounts/admin/household"] =
            "refuses: behind the session check (forward_auth channel=admin), which refuses a suspended household's session",
        ["/accounts/admin/operator"] = "exempt: the operator's, of the home household, which is never suspended",
        ["/accounts/internal/jwks"] = "exempt: public keys, no household's",
        ["/accounts/internal/resolve"] = "refuses: SuspendedHouseholdAnswer (423), IdentityResolution",
        ["/accounts/internal/guest"] = "refuses: SuspendedHouseholdAnswer (423), IdentityResolution",
        ["/accounts/internal/session"] =
            "exempt: answers deliberately, so the agent refuses the turn with SuspendedAnswer rather than 'signed out'",
        ["/accounts/internal/telegram/start"] = "refuses: TelegramStartOutcome.Suspended (423), nothing spent",
        ["/accounts/internal/telegram/web-link"] = "refuses: TelegramWebLinkOutcome.Suspended (423)",
        ["/accounts/internal/telegram/bind"] =
            "exempt: binds a group to the household its owner's code names; nothing in the group runs while suspended",
        ["/accounts/internal/telegram/group"] =
            "exempt: names the group's household; the bot then resolves its sender, which is refused",
        ["/accounts/internal/telegram/of-account"] =
            "exempt: names a linked Telegram for an approval a turn asked, and no turn of a suspended household runs",
        ["/accounts/internal/tunnels/verify"] = "refuses: TunnelCredentialRefusal.Suspended, TunnelCredentialStore",
        ["/accounts/internal/tunnels/holders"] =
            "exempt: names a suspended household's servers to the key commands on purpose, so its delegations are not stranded by a retirement",
        ["/accounts/internal/household-config/agent"] =
            "refuses: the agent's view says Suspended, which the agent refuses turns, fires, dreaming and extraction by",
        ["/accounts/internal/household-config/people"] =
            "exempt: dreaming's walk, which reads Suspended off each household's agent view",
        ["/accounts/internal/household-config"] =
            "exempt: a shared server's own section, read for a call the agent made, and the agent runs no suspended household's turn",
        ["/accounts/internal/household-config-seed"] = "exempt: the household migration's, run by the operator",
        ["/accounts/internal/household-import"] = "exempt: the household migration's, run by the operator"
    };

    [Fact]
    public async Task EveryRoute_SaysWhatItDoesForASuspendedHousehold()
    {
        await using var server = await AccountsServer.StartAsync();
        var routes = Routes(server);

        routes.Where(route => CoverageOf(route) is null).ShouldBeEmpty(
            "every accounts route is placed in the suspension coverage list, with how it refuses a suspended household or why it need not");
    }

    [Fact]
    public async Task EveryEntry_StillCoversARoute()
    {
        await using var server = await AccountsServer.StartAsync();
        var routes = Routes(server);

        _coverage.Keys.Where(prefix => !routes.Any(route => Covers(prefix, route))).ShouldBeEmpty(
            "an entry no route is under any more is left over from a route that went");
    }

    private static IReadOnlyList<string> Routes(AccountsServer server) =>
    [
        .. server.Service<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!)
            .Distinct()
    ];

    private static string? CoverageOf(string route) =>
        _coverage.Where(entry => Covers(entry.Key, route)).MaxBy(entry => entry.Key.Length).Value;

    private static bool Covers(string prefix, string route) =>
        route == prefix || route.StartsWith(prefix + "/", StringComparison.Ordinal);
}