using Accounts.Configuration;
using Domain.Agents;
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
public static class HouseholdSpacesAdmin
{
    public static void Map(RouteGroupBuilder household)
    {
        var path = AdminApi.Under(AdminProtocol.Household, AdminProtocol.Spaces);

        household.MapGet(path, async (AdminCaller caller, HouseholdConfigStore store, CancellationToken ct) =>
            await AnswerAsync(store, caller, ct) is { } spaces ? Results.Ok(spaces) : Results.NotFound());

        household.MapPut(path, async (
            AdminCaller caller, [FromBody] SpacesSection? sent, HttpContext http, HouseholdConfigStore store, AdminLog log,
            CancellationToken ct) =>
        {
            if (sent?.Spaces is null)
            {
                return Results.BadRequest(new AdminRefusal("The spaces were not saved: send them as the spaces section.", []));
            }

            sent = sent with { Spaces = [.. sent.Spaces.Select(s => s with { Name = s.Name?.Trim() ?? "" })] };

            if (await store.ReadAsync(caller.HouseholdId, HouseholdSections.SpacesSchema, ct) is not { } before
                || await AgentsAsync(store, caller, ct) is not { } agents)
            {
                return Results.NotFound();
            }

            if (before.Spaces.FirstOrDefault(s => s.Slug == SpaceConfig.DefaultSlug) is { } main
                && sent.Spaces.All(s => s.Slug != SpaceConfig.DefaultSlug))
            {
                return Results.BadRequest(new AdminRefusal(
                    $"{main.Name} is the default space, where a removed space's conversations are kept, so it stays: rename or recolour it instead.",
                    []));
            }

            var problems = sent.Spaces.SelectMany(space => Problems(space, agents)).ToList();
            if (problems.Count > 0)
            {
                return Results.BadRequest(new AdminRefusal("The spaces were not saved.", problems));
            }

            return await AdminSections.AnswerAsync(
                await store.SaveAsync(caller.HouseholdId, HouseholdSections.SpacesSchema, HouseholdSections.SpacesSchema.Write(sent), ct),
                "The spaces were not saved.",
                async saved =>
                {
                    var after = (await store.ReadAsync(caller.HouseholdId, HouseholdSections.SpacesSchema, ct))!;
                    await log.RecordAsync(http, caller, Summary(before, after), ct);
                    return Results.Ok(new AdminSaved<AdminSpaces>(Answer(after, agents), AdminSections.Warning(saved)));
                });
        });
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

    private static async Task<AdminSpaces?> AnswerAsync(HouseholdConfigStore store, AdminCaller caller, CancellationToken ct) =>
        await store.ReadAsync(caller.HouseholdId, HouseholdSections.SpacesSchema, ct) is { } spaces
        && await AgentsAsync(store, caller, ct) is { } agents
            ? Answer(spaces, agents)
            : null;

    private static AdminSpaces Answer(SpacesSection spaces, IReadOnlyList<AdminSpaceAgent> agents) =>
        new(SpaceListing.Ordered(spaces.Spaces), agents);

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
        clauses.AddRange(after.Spaces.Where(s => !was.ContainsKey(s.Slug)).Select(s => $"added space {s.Slug}"));
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
            ("order", before.Order != after.Order));
}