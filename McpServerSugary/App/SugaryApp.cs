using System.Net.Http.Headers;
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
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace McpServerSugary.App;

// Her app and the HTTP API it calls, on every route but /mcp. These routes are called by her phone,
// so they carry the standalone app's own gate — an access code exchanged once for a long-lived
// cookie — rather than the deployment secret /mcp asks for.
public static class SugaryApp
{
    public static IServiceCollection AddSugaryApp(this IServiceCollection services, SugarySettings settings)
    {
        services.AddSingleton(settings.SugaryApp);
        services.TryAddSingleton(TimeProvider.System);
        services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        // The keys live beside the log, so a redeployed container still accepts the cookies it issued.
        services.AddDataProtection()
            .SetApplicationName("Sugary")
            .PersistKeysToFileSystem(new DirectoryInfo(KeysDirectory(settings)));

        services.AddHttpClient<OpenFoodFactsClient>(http =>
        {
            http.BaseAddress = new Uri(settings.OpenFoodFactsUrl);
            http.Timeout = TimeSpan.FromSeconds(15);
            // Open Food Facts asks every client to identify itself.
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sugary", "1.0"));
        });

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
        // The built app is the web root's: wwwroot beside the server in the image, filled by the
        // image's node stage (Sugary.Client).
        var files = new StaticFileOptions { OnPrepareResponse = context => CacheRule(context.Context) };
        app.UseDefaultFiles();
        app.UseStaticFiles(files);
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        var api = app.MapGroup("/api");
        api.MapSessionEndpoints();

        var guarded = api.MapGroup("").RequireAuthorization();
        guarded.MapMealEndpoints();
        guarded.MapFoodEndpoints();
        guarded.MapProductEndpoints();
        guarded.MapExportEndpoints();
        guarded.MapEstimateEndpoints();
        guarded.MapLabelEndpoints();

        api.MapFallback(() => Results.NotFound());
        // Any other path without a file name is one of the app's own screens.
        app.MapFallbackToFile("index.html", files);
        return app;
    }

    // Vite fingerprints everything under assets/; the rest (index.html, the service worker, the
    // manifest) must be revalidated or a phone keeps running an old build.
    private static void CacheRule(HttpContext context) =>
        context.Response.Headers.CacheControl = context.Request.Path.StartsWithSegments("/assets")
            ? "public, max-age=31536000, immutable"
            : "no-cache";

    private static string KeysDirectory(SugarySettings settings) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath)) ?? ".", "keys");
}