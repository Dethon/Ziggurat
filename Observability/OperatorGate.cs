using Domain.Security;
using Domain.Security.Signing;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace Observability;

// Memory text and usage per person are the operator's alone. The proxy asks the operator's session
// before every request here (`?role=operator&channel=dashboard`), and the dashboard asks again of
// what the proxy handed on: the accounts service's assertion, verified against its published keys
// and held to the dashboard's channel, never the three plain headers beside it, which anything on
// the compose network could write. In front of everything served — the client's files, the API and
// the live hub — so nothing added later is served without it.
public static class OperatorGate
{
    public static IServiceCollection AddOperatorGate(
        this IServiceCollection services, string jwksUrl, TokenVerificationSettings verification)
    {
        services.AddHttpClient();
        return services.AddSingleton(sp =>
        {
            var clients = sp.GetRequiredService<IHttpClientFactory>();
            return AssertionVerifier.OverJwks(
                ct => clients.CreateClient().GetStringAsync(jwksUrl, ct),
                sp.GetRequiredService<TimeProvider>(),
                verification,
                sp.GetService<ILoggerFactory>());
        });
    }

    // How the gate treats time is the deployment's one answer (Domain/token-verification.json), read
    // after this host's appsettings.json and before the environment, as every verifying host reads it:
    // put ahead of the environment the web host already added, so a single container can still be
    // overridden and neither the environment nor the user secrets behind it change rank.
    public static TokenVerificationSettings GetTokenVerification(this IConfigurationBuilder configBuilder)
    {
        var policy = new JsonConfigurationSource { Path = TokenVerificationSettings.FileName, Optional = true };
        policy.ResolveFileProvider();
        var environment = configBuilder.Sources.ToList().FindIndex(source => source is EnvironmentVariablesConfigurationSource);
        if (environment < 0)
        {
            configBuilder.Add(policy);
        }
        else
        {
            configBuilder.Sources.Insert(environment, policy);
        }

        return configBuilder.Build().GetSection(TokenVerificationSettings.Section).Get<TokenVerificationSettings>()
               ?? new TokenVerificationSettings();
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