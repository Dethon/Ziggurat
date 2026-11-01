using System.Text.Json.Nodes;
using Accounts.Configuration;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;

namespace Accounts.Admin;

// The household's name, time zone and language, read and saved by its owners over the settings
// section — through the same save and the same rules as the operator's set-household-config, so a
// value is held to one standard whoever sets it, and a save is announced, so the agent reads it on
// the household's next turn.
public static class HouseholdSettingsAdmin
{
    // A save the readers were not told of still stands; they read it once what they keep runs out.
    public const string NotToldWarning =
        "Saved, but the household's agents could not be told just now: they use the new settings within a minute.";

    public static void Map(RouteGroupBuilder household)
    {
        var path = AdminApi.Under(AdminProtocol.Household, AdminProtocol.Settings);

        household.MapGet(path, async (AdminCaller caller, HouseholdConfigStore store, CancellationToken ct) =>
            await store.ReadAsync(caller.HouseholdId, HouseholdSections.SettingsSchema, ct) is { } settings
                ? Results.Ok(settings)
                : Results.NotFound());

        household.MapPut(path, async (
            AdminCaller caller, JsonObject? document, HttpContext http, HouseholdConfigStore store, AdminLog log,
            CancellationToken ct) =>
        {
            if (document is null)
            {
                return Results.BadRequest(new AdminRefusal("The settings were not saved: send them as a JSON object.", []));
            }

            var before = await store.ReadAsync(caller.HouseholdId, HouseholdSections.SettingsSchema, ct);
            switch (await store.SaveAsync(caller.HouseholdId, HouseholdSections.SettingsSchema, document, ct))
            {
                case SectionSave.Invalid invalid:
                    return Results.BadRequest(new AdminRefusal("The settings were not saved.", invalid.Problems));
                case SectionSave.Saved saved:
                    var after = (await store.ReadAsync(caller.HouseholdId, HouseholdSections.SettingsSchema, ct))!;
                    await log.RecordAsync(http, caller, Summary(before, after), ct);
                    return Results.Ok(new AdminSaved<HouseholdSettings>(after, saved.NotTold is null ? null : NotToldWarning));
                default:
                    return Results.NotFound();
            }
        });
    }

    // Which settings changed, by name: the values are the household's own and not secret, but a
    // summary never carries a value, so the pattern a keys page copies cannot leak one.
    private static string Summary(HouseholdSettings? before, HouseholdSettings after)
    {
        var changed = new (string Name, bool Changed)[]
        {
            ("name", before?.Name != after.Name),
            ("time zone", before?.TimeZone != after.TimeZone),
            ("language", before?.Language != after.Language)
        }.Where(f => f.Changed).Select(f => f.Name).ToList();
        return changed.Count == 0 ? "Saved the settings unchanged" : $"Changed the {string.Join(", ", changed)}";
    }
}