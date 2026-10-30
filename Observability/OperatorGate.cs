using Domain.Security;

namespace Observability;

// Memory text and usage per person are the operator's alone. The proxy asks the operator's session
// before every request here (`?role=operator&channel=dashboard`), and the dashboard asks again of
// what the proxy handed on: the accounts service's assertion, verified against its published keys
// and held to the dashboard's channel, never the three plain headers beside it, which anything on
// the compose network could write. In front of everything served — the client's files, the API and
// the live hub — so nothing added later is served without it.
public static class OperatorGate
{
    public static IServiceCollection AddOperatorGate(this IServiceCollection services, string jwksUrl)
    {
        services.AddHttpClient();
        return services.AddSingleton(sp =>
        {
            var clients = sp.GetRequiredService<IHttpClientFactory>();
            return AssertionVerifier.OverJwks(
                ct => clients.CreateClient().GetStringAsync(jwksUrl, ct),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<ILoggerFactory>());
        });
    }

    public static IApplicationBuilder UseOperatorGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var assertion = context.Request.Headers[ForwardedAccount.AssertionHeader] is [{ } value] ? value : null;
            var person = await context.RequestServices.GetRequiredService<AssertionVerifier>()
                .VerifyAsync(assertion, AppChannels.Dashboard, context.RequestAborted);
            if (person is not { IsOperator: true })
            {
                context.Response.StatusCode = person is null
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });
}