using System.Text.Json;
using System.Text.Json.Nodes;
using Accounts.Configuration;
using Accounts.Members;
using Domain.Agents;
using Domain.Apps;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.Mvc;

namespace Accounts.Admin;

// The household's chat-client spaces, read and saved by its owners as the spaces section whole: add,
// rename, recolour, reorder, remove, and the agent each opens on — through the same save and rules as
// the operator's set-household-config, announced, so the chat hub tells the household's next
// connection. Beyond the section's rules: the default space stays (a removed space's topics are
// listed there, an alarm's push lands there, the chat's own address opens it), a name is held to
// AdminSpaces.NameLength, and a space opens on one of the household's agents or on none.
//
// An app space shares one of the household's published apps (HouseholdSpace.AppId) with every member.
// The apps server is asked which those are (HouseholdAppsClient) only for a household the operator gave
// the apps feature, and its not answering costs only a new app space: the page still loads and saves. An
// app space's name and colour are its app's, stamped from the apps server's answer and never the page's
// word; what a space is — the app it shares, or that it shares none — is set when it is added. The reach an
// owner approved for it (what it reads and talks to) is what the page sent, capped at what the app asks.
public static class HouseholdSpacesAdmin
{
    public static void Map(RouteGroupBuilder household)
    {
        var path = AdminApi.Under(AdminProtocol.Household, AdminProtocol.Spaces);

        household.MapGet(path, async (
                AdminCaller caller, HouseholdConfigStore store, MemberStore members, HouseholdAppsClient apps, CancellationToken ct) =>
            await store.ReadAsync(caller.HouseholdId, HouseholdSections.SpacesSchema, ct) is { } spaces
            && await AgentsAsync(store, caller, ct) is { } agents
                ? Results.Ok(await AnswerAsync(spaces, agents, await PublishedAsync(store, apps, caller, ct), members, caller, ct))
                : Results.NotFound());

        household.MapPut(path, async (
            AdminCaller caller, [FromBody] JsonObject? document, HttpContext http, HouseholdConfigStore store, MemberStore members,
            HouseholdAppsClient apps, AdminLog log, CancellationToken ct) =>
        {
            if (document is null)
            {
                return Results.BadRequest(_notASection);
            }

            if (await store.ReadAsync(caller.HouseholdId, HouseholdSections.SpacesSchema, ct) is not { } before
                || await AgentsAsync(store, caller, ct) is not { } agents)
            {
                return Results.NotFound();
            }

            // A space is merged by its slug: another owner's space added, changed or removed since this
            // page was opened stands beside this page's own changes.
            var merged = AdminSections.Merged(document, HouseholdSections.SpacesSchema.Write(before), _bySlug);
            if (merged.Conflicts.Count > 0)
            {
                return AdminSections.Conflict("The spaces were not saved.", merged,
                    path => path.Count > 1 ? $"space {path[1]}" : "the spaces");
            }

            if (Section(merged.Value!.AsObject()) is not { Spaces: not null } sent)
            {
                return Results.BadRequest(_notASection);
            }

            sent = sent with { Spaces = [.. sent.Spaces.Select(s => s with { Name = s.Name?.Trim() ?? "" })] };

            if (before.Spaces.FirstOrDefault(s => s.Slug == SpaceConfig.DefaultSlug) is { } main
                && sent.Spaces.All(s => s.Slug != SpaceConfig.DefaultSlug))
            {
                return Results.BadRequest(new AdminRefusal(
                    $"{main.Name} is the default space, where a removed space's conversations are kept, so it stays: rename or recolour it instead.",
                    []));
            }

            var problems = sent.Spaces.SelectMany(space => Problems(space, agents))
                .Concat(sent.Spaces.SelectMany(space => KindProblems(space, before))).ToList();
            if (problems.Count > 0)
            {
                return Results.BadRequest(new AdminRefusal("The spaces were not saved.", problems));
            }

            var added = sent.Spaces.Where(s => s.IsApp && before.Spaces.All(b => b.Slug != s.Slug)).ToList();
            var approving = sent.Spaces.Where(s => s.IsApp && !Equals(s.Approved, before.Spaces.FirstOrDefault(b => b.Slug == s.Slug)?.Approved))
                .Select(s => s.Slug).ToHashSet(StringComparer.Ordinal);
            // Asked whatever the save holds, since the answer is what the page shows next: the apps it may share.
            var published = await PublishedAsync(store, apps, caller, ct);
            if ((added.Count > 0 || approving.Count > 0) && published is null)
            {
                return Results.Json(new AdminRefusal(AdminSpaces.AppsUnavailableMessage, []),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            problems = [.. added.Where(s => published!.All(app => app.Id != s.AppId))
                .Select(s => $"space {s.Slug} shares app {s.AppId}, which is none of the household's published apps")];
            if (problems.Count > 0)
            {
                return Results.BadRequest(new AdminRefusal("The spaces were not saved.", problems));
            }

            sent = sent with
            {
                Spaces = [.. sent.Spaces.Select(space => Approved(Stamped(space, before, published), approving, before, published))]
            };

            return await AdminSections.AnswerAsync(
                await store.SaveAsync(caller.HouseholdId, HouseholdSections.SpacesSchema, HouseholdSections.SpacesSchema.Write(sent), ct),
                "The spaces were not saved.",
                async saved =>
                {
                    var after = (await store.ReadAsync(caller.HouseholdId, HouseholdSections.SpacesSchema, ct))!;
                    await log.RecordAsync(http, caller, Summary(before, after), ct);
                    return Results.Ok(new AdminSaved<AdminSpaces>(
                        await AnswerAsync(after, agents, published, members, caller, ct), AdminSections.Warning(saved)));
                });
        });
    }

    private static readonly AdminRefusal _notASection = new("The spaces were not saved: send them as the spaces section.", []);

    private static readonly IReadOnlyDictionary<string, string> _bySlug = new Dictionary<string, string> { ["spaces"] = "slug" };

    private static SpacesSection? Section(JsonObject document)
    {
        document.Remove(SectionSchema.VersionProperty);
        try
        {
            return document.Deserialize<SpacesSection>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Problems(HouseholdSpace space, IReadOnlyList<AdminSpaceAgent> agents)
    {
        if (space.Name?.Trim().Length > AdminSpaces.NameLength)
        {
            yield return $"space {space.Slug}'s name must be 1–{AdminSpaces.NameLength} characters";
        }

        if (space.DefaultAgent is { } agent && agents.All(a => a.Id != agent))
        {
            yield return $"space {space.Slug} opens on '{agent}', which is none of the household's agents";
        }
    }

    // What a space is stays what it was added as: an app space shares the app it was added with, and a
    // chat space never becomes one, which would leave its conversations in a space nobody joins.
    private static IEnumerable<string> KindProblems(HouseholdSpace space, SpacesSection before)
    {
        if (before.Spaces.FirstOrDefault(b => b.Slug == space.Slug) is not { } was || was.AppId == space.AppId)
        {
            yield break;
        }

        yield return was.IsApp
            ? $"space {space.Slug} shares app {was.AppId}, which is set when the space is added: add another space for another app"
            : $"space {space.Slug} holds conversations, so it shares no app: add another space for the app";
    }

    // An app space's name and colour are its app's as the apps server answers now, else as last saved.
    private static HouseholdSpace Stamped(HouseholdSpace space, SpacesSection before, IReadOnlyList<PublishedApp>? published) =>
        space.IsApp && published?.FirstOrDefault(app => app.Id == space.AppId) is { } app
            ? space with { Name = app.Name, AccentColor = app.AccentColor }
            : space.IsApp && before.Spaces.FirstOrDefault(b => b.Slug == space.Slug) is { } was
                ? space with { Name = was.Name, AccentColor = was.AccentColor }
                : space;

    // An app space's approved reach is only ever what an owner's save sent, never what the app asks at the
    // time of the save — a publish landing between the page loading and saving it is not approved unseen —
    // and no more of it than the app asks now. A save leaving it as loaded leaves it as stored. Nothing
    // approved is none.
    private static HouseholdSpace Approved(
        HouseholdSpace space, IReadOnlySet<string> approving, SpacesSection before, IReadOnlyList<PublishedApp>? published)
    {
        if (!approving.Contains(space.Slug))
        {
            return space;
        }

        if (published?.FirstOrDefault(app => app.Id == space.AppId) is not { } app)
        {
            return space with { Approved = before.Spaces.FirstOrDefault(b => b.Slug == space.Slug)?.Approved };
        }

        var approved = app.Reach.Within(space.Approved);
        return space with { Approved = approved.IsEmpty ? null : approved };
    }

    // The household's published apps, none for a household without the apps feature (the apps server is
    // not asked), null when the apps server could not be. The apps page's too (HouseholdAppAsksAdmin).
    internal static async Task<IReadOnlyList<PublishedApp>?> PublishedAsync(
        HouseholdConfigStore store, HouseholdAppsClient apps, AdminCaller caller, CancellationToken ct) =>
        await store.ReadAsync(caller.HouseholdId, HouseholdSections.FeaturesSchema, ct) is { Apps: true }
            ? await apps.PublishedAsync(caller.Person, ct)
            : [];

    private static async Task<AdminSpaces> AnswerAsync(
        SpacesSection spaces, IReadOnlyList<AdminSpaceAgent> agents, IReadOnlyList<PublishedApp>? published, MemberStore members,
        AdminCaller caller, CancellationToken ct)
    {
        if (published is null)
        {
            return new AdminSpaces(SpaceListing.Ordered(spaces.Spaces), agents);
        }

        var names = published.Count == 0
            ? new Dictionary<string, string>()
            : (await members.ListAsync(caller.HouseholdId, ct)).ToDictionary(m => m.Id, m => m.DisplayName);
        return new AdminSpaces(
            SpaceListing.Ordered(spaces.Spaces.Select(space => Stamped(space, spaces, published))),
            agents,
            [
                .. published.Select(app => new AdminSpaceApp(app.Id, app.Slug, app.Name, app.AccentColor, app.Description, app.AuthorId,
                    names.GetValueOrDefault(app.AuthorId, ""), app.Reads, app.Connect, app.Url) { Asks = app.Asks })
            ]);
    }

    // The agents the household's turns run, under their names: what a space may open on.
    private static async Task<IReadOnlyList<AdminSpaceAgent>?> AgentsAsync(
        HouseholdConfigStore store, AdminCaller caller, CancellationToken ct) =>
        await store.ReadAsync(caller.HouseholdId, HouseholdSections.AgentsSchema, ct) is { } section
            ? [.. HouseholdAgents.Names(section).Agents.Select(a => new AdminSpaceAgent(a.Id, a.Name))]
            : null;

    // What changed, space by space and named by slug: a space's name and colour are the household's
    // own, but a summary never carries a value.
    private static string Summary(SpacesSection before, SpacesSection after)
    {
        var was = before.Spaces.ToDictionary(s => s.Slug);
        var clauses = new List<string>();
        clauses.AddRange(after.Spaces.Where(s => !was.ContainsKey(s.Slug))
            .Select(s => s.AppId is { } app ? $"added space {s.Slug}, sharing app {app}" : $"added space {s.Slug}"));
        clauses.AddRange(before.Spaces.Where(s => after.Spaces.All(a => a.Slug != s.Slug)).Select(s => $"removed space {s.Slug}"));
        clauses.AddRange(after.Spaces.Where(s => was.ContainsKey(s.Slug)).Select(s => (s.Slug, Fields: Changed(was[s.Slug], s)))
            .Where(c => c.Fields.Count > 0)
            .Select(c => $"changed space {c.Slug}'s {string.Join(", ", c.Fields)}"));
        var summary = clauses.Count == 0 ? "saved the spaces unchanged" : string.Join("; ", clauses);
        return char.ToUpperInvariant(summary[0]) + summary[1..];
    }

    private static IReadOnlyList<string> Changed(HouseholdSpace before, HouseholdSpace after) =>
        AdminLog.Changed(
            ("name", before.Name != after.Name),
            ("colour", before.AccentColor != after.AccentColor),
            ("default agent", before.DefaultAgent != after.DefaultAgent),
            ("order", before.Order != after.Order),
            ("approved reach", !Equals(before.Approved, after.Approved)));
}