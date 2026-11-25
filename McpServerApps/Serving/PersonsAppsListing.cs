using Domain.Apps;
using Domain.Security;
using McpServerApps.Settings;
using McpServerApps.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerApps.Serving;

// A person's apps for WebChat's space switcher (PersonsApps), on the MCP listener and outside /mcp, so the
// server's own secret does not gate it: what does is an assertion the accounts service signed for a person
// on AppChannels.PersonsApps, which it signs for the chat hub's session refresh alone and no proxy block
// names. The person and household are that assertion's. Answered: their own published apps, and of the
// ids the request names as shared — the app spaces the hub read in the household's spaces — those the
// household has published. A named id is never more than its link and name, which every member of a
// household sharing it opens anyway.
internal sealed class PersonsAppsListing(AppListeners listeners) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, inner) =>
        {
            if (context.Connection.LocalPort == listeners.Mcp && context.Request.Path == PersonsApps.Path
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
                    .AdmitAsync(context.Request.Headers, AppChannels.PersonsApps, _ => true, ct))
        {
            case AppAdmission.Admitted { Person: var person }:
                var shared = context.Request.Query[PersonsApps.SharedParameter].OfType<string>().ToHashSet(StringComparer.Ordinal);
                var store = services.GetRequiredService<BuiltAppStore>();
                var domain = services.GetRequiredService<AppsSettings>().AppsDomain;
                var listed = await Task.WhenAll((await store.ListAsync(person.TenantId, ct))
                    .Where(built => built.Author == person.Account || shared.Contains(built.Id))
                    .Select(async built => await store.PublishedManifestAsync(built, ct) is { } manifest
                        ? new PersonsApp(built.Id, manifest.Name, manifest.AccentColor,
                            BuiltAppId.Link(built.Slug, built.Id, domain), Own: built.Author == person.Account)
                        : null));
                await response.WriteAsJsonAsync(new PersonsAppList([.. listed.OfType<PersonsApp>()]), ct);
                return;
            case var refused:
                response.StatusCode = refused.Status;
                return;
        }
    }
}

public static class PersonsAppsListingExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddPersonsAppsListing(AppListeners listeners) =>
            services.AddSingleton<IStartupFilter>(new PersonsAppsListing(listeners));
    }
}