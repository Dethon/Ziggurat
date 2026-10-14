using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using McpServerSugary.Settings;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerSugary.App;

// Her app and the HTTP API it calls, on every route but /mcp. These routes are called by her phone,
// so they carry the standalone app's own gate — an access code exchanged once for a long-lived
// cookie — rather than the deployment secret /mcp asks for.
public static class SugaryApp
{
    public static IServiceCollection AddSugaryApp(this IServiceCollection services, SugarySettings settings)
    {
        services.AddSingleton(settings.SugaryApp);
        services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        // The keys live beside the log, so a redeployed container still accepts the cookies it issued.
        services.AddDataProtection()
            .SetApplicationName("Sugary")
            .PersistKeysToFileSystem(new DirectoryInfo(KeysDirectory(settings)));

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(cookie =>
        {
            cookie.Cookie.Name = "sugary";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.ExpireTimeSpan = TimeSpan.FromDays(180);
            cookie.SlidingExpiration = true;
            // An API answers a missing cookie with 401, which is what the app listens for; a redirect
            // to a sign-in page would come back as an HTML 200.
            cookie.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            cookie.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        services.AddAuthorization();
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // One shared window rather than one per client: behind a proxy every caller can look
            // alike, and only two people are ever meant to sign in.
            limiter.AddPolicy(SessionEndpoints.SignInRateLimit, _ => RateLimitPartition.GetFixedWindowLimiter(
                "all", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });

        return services;
    }

    public static WebApplication MapSugaryApp(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        var api = app.MapGroup("/api");
        api.MapSessionEndpoints();

        var guarded = api.MapGroup("").RequireAuthorization();
        guarded.MapMealEndpoints();

        api.MapFallback(() => Results.NotFound());
        return app;
    }

    private static string KeysDirectory(SugarySettings settings) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath)) ?? ".", "keys");
}