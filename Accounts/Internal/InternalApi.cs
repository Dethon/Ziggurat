using Accounts.Settings;
using Domain.Security.Signing;

namespace Accounts.Internal;

// Everything under /accounts/internal is server to server: a caller presents the credential it
// already holds for its own /mcp (CALLERS__<host>), and anything else is refused before a row is
// read. The filter is the group's, so an endpoint added here is covered without saying so. The
// proxy answers every one of these paths 404 from outside, before /accounts reaches this service.
public static class InternalApi
{
    public const string Path = "/internal";

    // The public keys assertions verify with, for the agent and the apps behind the proxy.
    public const string JwksPath = AccountsApp.PathBase + Path + Jwks;
    private const string Jwks = "/jwks";

    public static void Map(IEndpointRouteBuilder accounts)
    {
        // The one route here asked without a credential: public keys are no secret, and the
        // verifiers that fetch them (the agent, the apps) hold no CALLERS entry. Under this path all
        // the same, because every host that reaches the service answers it 404 from outside.
        accounts.MapGet(Path + Jwks, (HttpContext http, SigningKeyStore keys) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Content(keys.PublishedKeys.ToJson(), "application/json");
        });

        // Who is asking is settled here, once; what each caller may ask about is each endpoint's,
        // since only the endpoint knows which channel a question is about (InternalCallers).
        var routes = accounts.MapGroup(Path).AddEndpointFilter(async (context, next) =>
        {
            var settings = context.HttpContext.RequestServices.GetRequiredService<AccountsSettings>();
            return InternalCallers.Presenting(context.HttpContext, settings).Count > 0
                ? await next(context)
                : Results.Unauthorized();
        });
        IdentityResolution.Map(routes);
        SessionRefresh.Map(routes);
        TelegramLinking.Map(routes);
        HouseholdImportApi.Map(routes);
    }
}