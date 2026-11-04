using Domain.DTOs.Metrics;
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

    // One path is not the operator's: the usage route (HouseholdUsage.Path), which the accounts
    // service's admin API asks for an owner, with an assertion it signs for the usage channel — a
    // channel no proxy block names, so no browser is ever handed one. It is held to that channel and
    // the owner role, and the owner is kept for the route, which answers their household and no other.
    // Everything else is held to the dashboard's channel and the operator role, as it always was.
    //
    // And one kind of request is nobody's: a file of the client's own, asked for by something that is
    // not a page being opened (ClientFile). An installed dashboard replaces its service worker by
    // fetching the worker's script and installs the new one by fetching these files, signed out as often
    // as not, and the worker from before sign-in answers every page with its cached app — so without
    // them an operator's installed dashboard was stuck on that for good. They hold no figure; a path
    // that is not a file in the web root, the API, the hub and every page stay behind the gate.
    public static IApplicationBuilder UseOperatorGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (ClientFile(context))
            {
                await next(context);
                return;
            }

            var usage = context.Request.Path.Equals(HouseholdUsage.Path);
            var admission = await context.RequestServices.GetRequiredService<AssertionVerifier>()
                .AdmitAsync(
                    context.Request.Headers, usage ? AppChannels.Usage : AppChannels.Dashboard,
                    person => usage ? person.IsOwner : person.IsOperator, context.RequestAborted);
            if (admission is not AppAdmission.Admitted { Person: var person })
            {
                context.Response.StatusCode = admission.Status;
                return;
            }

            if (usage)
            {
                UsageOwner.Keep(context, person);
            }

            await next(context);
        });

    private static bool ClientFile(HttpContext context)
    {
        var request = context.Request;
        return HttpMethods.IsGet(request.Method)
               && !string.Equals(request.Headers["Sec-Fetch-Mode"], "navigate", StringComparison.OrdinalIgnoreCase)
               && !request.Headers.Accept.Any(a => a?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
               && !request.Path.StartsWithSegments("/api")
               && !request.Path.StartsWithSegments("/hubs")
               && request.Path.HasValue
               && context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider
                   .GetFileInfo(request.Path.Value) is { Exists: true, IsDirectory: false };
    }
}

// The owner the gate verified for the usage route, kept on that request alone and bound by the route
// as a parameter: a request the gate did not admit for the usage channel binds none, and is refused.
public sealed record UsageOwner(ForwardedAccount Person)
{
    private static readonly object _item = new();

    public string HouseholdId => Person.TenantId;

    public static ValueTask<UsageOwner?> BindAsync(HttpContext http) =>
        ValueTask.FromResult(http.Items.TryGetValue(_item, out var owner) ? owner as UsageOwner : null);

    internal static void Keep(HttpContext http, ForwardedAccount person) => http.Items[_item] = new UsageOwner(person);
}