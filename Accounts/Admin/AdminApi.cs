using Domain.DTOs.Admin;
using Domain.Security;

namespace Accounts.Admin;

// The admin API (.claude/rules/admin.md): what the owner's admin area in the chat client and the
// operator's household list in the dashboard read and change, under two roots, both behind one gate.
//
// The proxy puts /accounts/admin/* behind its session check for the admin channel, which signs who
// the request is from; the gate believes that assertion — verified against this service's own keys,
// held to the admin channel — and nothing else: not the plain headers beside it, which anything on
// the compose network could write, nor the session cookie, which a sibling host's page sends too.
// The gate stands in front of every path under the admin root, before any route is chosen, so a
// route added later is covered without saying so, and a path under neither root is nobody's.
public static class AdminApi
{
    private static readonly AdminRefusal _nobodys = new("There is no such admin route.", []);

    // Before the routes: who the request is from has to be settled before a route binds its caller.
    public static void UseGate(IApplicationBuilder app) =>
        app.UseWhen(http => http.Request.Path.StartsWithSegments(AdminProtocol.Root), admin => admin.Use(GateAsync));

    public static void Map(IEndpointRouteBuilder accounts)
    {
        var household = accounts.MapGroup(Relative(AdminProtocol.Household));
        var deployment = accounts.MapGroup(Relative(AdminProtocol.Operator));

        HouseholdSettingsAdmin.Map(household);
        HouseholdAgentsAdmin.Map(household);
        AdminLog.Map(household, deployment);
    }

    // A route's path inside its group, from the one spelling in AdminProtocol.
    public static string Under(string root, string path) =>
        path.StartsWith(root, StringComparison.Ordinal)
            ? path[root.Length..]
            : throw new ArgumentException($"{path} is not under {root}", nameof(path));

    // Every route is mapped under /accounts (AccountsApp.PathBase), which the protocol's paths begin with.
    private static string Relative(string path) => Under(AccountsApp.PathBase, path);

    // In order: the admin client's header (403 without it: the request did not come from a page on
    // this host), the assertion (401: signed out, or an assertion signed for another channel), then
    // the root's role (403). Nothing here is kept by a browser or a proxy.
    private static async Task GateAsync(HttpContext http, RequestDelegate next)
    {
        http.Response.Headers.CacheControl = "no-store";
        var path = http.Request.Path;
        Func<ForwardedAccount, bool>? allowed = path.StartsWithSegments(AdminProtocol.Household) ? person => person.IsOwner
            : path.StartsWithSegments(AdminProtocol.Operator) ? person => person.IsOperator
            : null;
        if (allowed is null)
        {
            await Results.Json(_nobodys, statusCode: StatusCodes.Status404NotFound).ExecuteAsync(http);
            return;
        }

        if (http.Request.Headers[AdminProtocol.RequestHeader] != AdminProtocol.RequestHeaderValue)
        {
            await Results.Json(
                    new AdminRefusal($"An admin request carries {AdminProtocol.RequestHeader}: {AdminProtocol.RequestHeaderValue}.", []),
                    statusCode: StatusCodes.Status403Forbidden)
                .ExecuteAsync(http);
            return;
        }

        var person = await http.RequestServices.GetRequiredService<AssertionVerifier>()
            .VerifyRequestAsync(http.Request.Headers, AppChannels.Admin, http.RequestAborted);
        if (person is null)
        {
            await Results.Unauthorized().ExecuteAsync(http);
            return;
        }

        if (!allowed(person))
        {
            var refusal = path.StartsWithSegments(AdminProtocol.Household)
                ? "Only the household's owners manage it."
                : "Only the deployment's operator manages households.";
            await Results.Json(new AdminRefusal(refusal, []), statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(http);
            return;
        }

        AdminCaller.Keep(http, new AdminCaller(person));
        await next(http);
    }
}