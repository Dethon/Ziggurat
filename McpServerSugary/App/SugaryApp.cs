using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpServerSugary.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace McpServerSugary.App;

// The app and the HTTP API it calls, on every route but /mcp. These routes are called by a person's
// phone through the proxy, whose session check names the person in a signed assertion (Person); /mcp is
// the deployment's and keeps its own secret. There is no sign-in here: signing in is the accounts
// service's, once for every app of the deployment.
public static class SugaryApp
{
    public static IServiceCollection AddSugaryApp(this IServiceCollection services, SugarySettings settings)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        services.AddHttpClient<OpenFoodFactsClient>(http =>
        {
            http.BaseAddress = new Uri(settings.OpenFoodFactsUrl);
            http.Timeout = TimeSpan.FromSeconds(15);
            // Open Food Facts asks every client to identify itself.
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sugary", "1.0"));
        });

        return services;
    }

    public static WebApplication MapSugaryApp(this WebApplication app)
    {
        // The built app is the web root's: wwwroot beside the server in the image, filled by the
        // image's node stage (Sugary.Client). The app itself holds nobody's data, so it is served to
        // anyone the proxy lets through; everything a person keeps is under /api.
        var files = new StaticFileOptions { OnPrepareResponse = context => CacheRule(context.Context) };
        app.UseDefaultFiles();
        app.UseStaticFiles(files);

        // Before any route binds its person, so a request naming nobody is told so plainly — a 401,
        // which is what sends the app to sign in — rather than a route's 400 for a missing parameter.
        // The assertion is verified here, once a request, and every route binds the person it named.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api")
                && await Person.SignedInAsync(context, context.RequestAborted) is null)
            {
                await Results.Problem("Not signed in.", statusCode: StatusCodes.Status401Unauthorized)
                    .ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        var api = app.MapGroup("/api");
        api.MapSessionEndpoints();
        api.MapMealEndpoints();
        api.MapDoseEndpoints();
        api.MapPenEndpoints();
        api.MapPushEndpoints();
        api.MapExerciseEndpoints();
        api.MapPeriodEndpoints();
        api.MapDayNoteEndpoints();
        api.MapFoodEndpoints();
        api.MapRecipeEndpoints();
        api.MapProductEndpoints();
        api.MapExportEndpoints();
        api.MapEstimateEndpoints();
        api.MapLabelEndpoints();

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
}