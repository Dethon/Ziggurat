using Accounts.Settings;

namespace Accounts.Internal;

// Everything under /accounts/internal is server to server: a caller presents the credential it
// already holds for its own /mcp (CALLERS__<host>), and anything else is refused before a row is
// read. The filter is the group's, so an endpoint added here is covered without saying so. The
// proxy answers every one of these paths 404 from outside, before /accounts reaches this service.
public static class InternalApi
{
    public const string Path = "/internal";

    public static void Map(IEndpointRouteBuilder accounts)
    {
        var routes = accounts.MapGroup(Path).AddEndpointFilter(async (context, next) =>
        {
            var settings = context.HttpContext.RequestServices.GetRequiredService<AccountsSettings>();
            var presented = context.HttpContext.Request.Headers.Authorization.ToString();
            return settings.Callers.Values.Any(credential => CallerCredential.Matches(presented, credential))
                ? await next(context)
                : Results.Unauthorized();
        });
        IdentityResolution.Map(routes);
        TelegramLinking.Map(routes);
    }
}