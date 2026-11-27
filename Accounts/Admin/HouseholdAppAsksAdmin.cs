using System.Text.Json;
using System.Text.Json.Nodes;
using Accounts.Configuration;
using Accounts.Members;
using Domain.Apps;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Microsoft.AspNetCore.Mvc;

namespace Accounts.Admin;

// The household's daily caps on its built apps' asks, read and saved by its owners as the app asks section
// whole — the household's default and each app's own — through the same save and rules as the operator's
// set-household-config, announced, so the apps server reads them again at the next ask. Beyond the section's
// rules: a cap of an app's own is set only for one of the household's published apps, as the apps server
// lists them now (HouseholdAppsClient, asked as the spaces page asks it). Its not answering costs only a cap
// set or changed for an app: the page still loads, and the default or a removal still saves. An app's cap
// left as loaded is never checked again, so an app since deleted blocks no other save.
public static class HouseholdAppAsksAdmin
{
    private const string NotSaved = "The app caps were not saved.";

    public static void Map(RouteGroupBuilder household)
    {
        var path = AdminApi.Under(AdminProtocol.Household, AdminProtocol.AppAsks);

        household.MapGet(path, async (
                AdminCaller caller, HouseholdConfigStore store, MemberStore members, HouseholdAppsClient apps, CancellationToken ct) =>
            await store.ReadAsync(caller.HouseholdId, HouseholdSections.AppAsksSchema, ct) is { } caps
                ? Results.Ok(await AnswerAsync(caps, await HouseholdPublishedApps.ReadAsync(store, apps, caller, ct), members, caller, ct))
                : Results.NotFound());

        household.MapPut(path, async (
            AdminCaller caller, [FromBody] JsonObject? document, HttpContext http, HouseholdConfigStore store, MemberStore members,
            HouseholdAppsClient apps, AdminLog log, CancellationToken ct) =>
        {
            if (document is null)
            {
                return Results.BadRequest(_notASection);
            }

            if (await store.ReadAsync(caller.HouseholdId, HouseholdSections.AppAsksSchema, ct) is not { } before)
            {
                return Results.NotFound();
            }

            // An app's cap is merged by its id, the object's own key: another owner's cap of another app set,
            // changed or removed since this page was opened stands beside this page's own.
            var merged = AdminSections.Merged(document, HouseholdSections.AppAsksSchema.Write(before));
            if (merged.Conflicts.Count > 0)
            {
                return AdminSections.Conflict(NotSaved, merged, Named);
            }

            if (Section(merged.Value!.AsObject()) is not { } sent)
            {
                return Results.BadRequest(_notASection);
            }

            var changed = sent.Apps.Where(app => !before.Apps.TryGetValue(app.Key, out var was) || was != app.Value)
                .Select(app => app.Key).ToList();
            // Asked whatever the save holds, since the answer is what the page shows next: the apps it may cap.
            var published = await HouseholdPublishedApps.ReadAsync(store, apps, caller, ct);
            if (changed.Count > 0 && published is null)
            {
                return Results.Json(new AdminRefusal(AdminSpaces.AppsUnavailableMessage, []),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var problems = changed.Where(app => BuiltAppId.IsValid(app) && published!.All(p => p.Id != app))
                .Select(app => $"App {app} is none of the household's published apps, so it has no cap of its own.").ToList();
            if (problems.Count > 0)
            {
                return Results.BadRequest(new AdminRefusal(NotSaved, problems));
            }

            return await AdminSections.AnswerAsync(
                await store.SaveAsync(caller.HouseholdId, HouseholdSections.AppAsksSchema, HouseholdSections.AppAsksSchema.Write(sent), ct),
                NotSaved,
                async saved =>
                {
                    var after = (await store.ReadAsync(caller.HouseholdId, HouseholdSections.AppAsksSchema, ct))!;
                    await log.RecordAsync(http, caller, Summary(before, after), ct);
                    return Results.Ok(new AdminSaved<AdminAppAsks>(
                        await AnswerAsync(after, published, members, caller, ct), AdminSections.Warning(saved)));
                });
        });
    }

    private static readonly AdminRefusal _notASection = new("The app caps were not saved: send them as the app asks section.", []);

    private static string Named(IReadOnlyList<string> path) => path switch
    {
        ["apps", var app, ..] => $"app {app}'s daily cap",
        ["dailyCap", ..] => "the household's daily cap",
        _ => "the app caps"
    };

    // Strict, as a stored section is read: a misspelt field is refused rather than dropped. An app whose cap one
    // page removed comes out of the merge as a null, which is no cap: gone, as the page meant.
    private static AppAsksSection? Section(JsonObject document)
    {
        document.Remove(SectionSchema.VersionProperty);
        if (document["apps"] is JsonObject caps)
        {
            caps.Where(cap => cap.Value is null).Select(cap => cap.Key).ToList().ForEach(app => caps.Remove(app));
        }

        try
        {
            return document.Deserialize<AppAsksSection>(SectionSchema.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<AdminAppAsks> AnswerAsync(
        AppAsksSection caps, IReadOnlyList<PublishedApp>? published, MemberStore members, AdminCaller caller, CancellationToken ct)
    {
        if (published is null)
        {
            return new AdminAppAsks(caps);
        }

        var authorOf = await HouseholdPublishedApps.AuthorNamesAsync(published, members, caller, ct);
        return new AdminAppAsks(caps,
            [.. published.Select(app => new AdminCappedApp(app.Id, app.Name, app.AccentColor, authorOf(app), app.Url))]);
    }

    // What changed, named by app id, never a cap's value.
    private static string Summary(AppAsksSection before, AppAsksSection after)
    {
        var clauses = Changed("the household's daily cap", before.DailyCap, after.DailyCap)
            .Concat(before.Apps.Keys.Union(after.Apps.Keys).Order(StringComparer.Ordinal)
                .SelectMany(app => Changed($"app {app}'s daily cap",
                    before.Apps.TryGetValue(app, out var was) ? was : null,
                    after.Apps.TryGetValue(app, out var now) ? now : null)))
            .ToList();
        var summary = clauses.Count == 0 ? "saved the app caps unchanged" : string.Join("; ", clauses);
        return char.ToUpperInvariant(summary[0]) + summary[1..];
    }

    private static IEnumerable<string> Changed(string what, int? before, int? after) => (before, after) switch
    {
        (null, null) => [],
        (null, _) => [$"set {what}"],
        (_, null) => [$"removed {what}"],
        _ when before != after => [$"changed {what}"],
        _ => []
    };
}