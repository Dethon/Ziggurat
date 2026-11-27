using Accounts.Configuration;
using Accounts.Members;
using Domain.Apps;
using Domain.DTOs.Household;

namespace Accounts.Admin;

// The household's published apps as the owners' pages show them — the spaces page, to share one, and the
// apps page, to cap one — asked of the apps server for the gate-verified owner (HouseholdAppsClient).
internal static class HouseholdPublishedApps
{
    // None for a household without the apps feature (the apps server is not asked), null when the apps server
    // could not be: a page still loads on null, and only what needs the listing is refused.
    public static async Task<IReadOnlyList<PublishedApp>?> ReadAsync(
        HouseholdConfigStore store, HouseholdAppsClient apps, AdminCaller caller, CancellationToken ct) =>
        await store.ReadAsync(caller.HouseholdId, HouseholdSections.FeaturesSchema, ct) is { Apps: true }
            ? await apps.PublishedAsync(caller.Person, ct)
            : [];

    // Each app's author by their name in the household now, empty for one no longer in it. The members are not
    // read when nothing is published.
    public static async Task<Func<PublishedApp, string>> AuthorNamesAsync(
        IReadOnlyList<PublishedApp> published, MemberStore members, AdminCaller caller, CancellationToken ct)
    {
        var names = published.Count == 0
            ? new Dictionary<string, string>()
            : (await members.ListAsync(caller.HouseholdId, ct)).ToDictionary(m => m.Id, m => m.DisplayName);
        return app => names.GetValueOrDefault(app.AuthorId, "");
    }
}