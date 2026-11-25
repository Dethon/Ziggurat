using Domain.Apps;
using Domain.Security;
using McpServerApps.Settings;
using McpServerApps.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerApps.Serving;

// The household's published apps for its owners' spaces page (PublishedApps), on the MCP listener and
// outside /mcp, so the server's own secret does not gate it: what does is an assertion the accounts
// service signed for an owner on AppChannels.PublishedApps, which it signs for nothing else and no proxy
// block names. The household is that assertion's. `apps-browser` reaches this listener too, and a check's
// page holds no such assertion.
internal sealed class PublishedAppsListing(AppListeners listeners) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, inner) =>
        {
            if (context.Connection.LocalPort == listeners.Mcp && context.Request.Path == PublishedApps.Path
                                                             && HttpMethods.IsGet(context.Request.Method))
            {
                await AnswerAsync(context);
                return;
            }

            await inner(context);
        });
        next(app);
    };

    private static async Task AnswerAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var services = context.RequestServices;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        switch (await services.GetRequiredService<AssertionVerifier>()
                    .AdmitAsync(context.Request.Headers, AppChannels.PublishedApps, person => person.IsOwner, ct))
        {
            case AppAdmission.Admitted { Person.TenantId: var household }:
                var store = services.GetRequiredService<BuiltAppStore>();
                var domain = services.GetRequiredService<AppsSettings>().AppsDomain;
                var listed = await Task.WhenAll((await store.ListAsync(household, ct)).Select(async built =>
                    await store.PublishedManifestAsync(built, ct) is { } manifest
                        ? new PublishedApp(built.Id, built.Slug, manifest.Name, manifest.AccentColor, manifest.Description,
                            built.Author.UserId, manifest.Reads, manifest.Connect, BuiltAppId.Link(built.Slug, built.Id, domain))
                        : null));
                await response.WriteAsJsonAsync(new PublishedAppList([.. listed.OfType<PublishedApp>()]), ct);
                return;
            case var refused:
                response.StatusCode = refused.Status;
                return;
        }
    }
}

public static class PublishedAppsListingExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddPublishedAppsListing(AppListeners listeners) =>
            services.AddSingleton<IStartupFilter>(new PublishedAppsListing(listeners));
    }
}