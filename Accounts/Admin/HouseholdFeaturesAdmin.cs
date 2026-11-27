using System.Text.Json.Nodes;
using Accounts.Configuration;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;

namespace Accounts.Admin;

// Which shared servers and domain features the household's agents are offered, read and saved by its
// owners as the features section whole — through the same save and rules as the operator's
// set-household-config, announced, so the agent builds the household's next turn without what was
// turned off (HouseholdAgents.Offered).
public static class HouseholdFeaturesAdmin
{
    public static void Map(RouteGroupBuilder household)
    {
        var path = AdminApi.Under(AdminProtocol.Household, AdminProtocol.Features);

        household.MapGet(path, async (AdminCaller caller, HouseholdConfigStore store, CancellationToken ct) =>
            await store.ReadAsync(caller.HouseholdId, HouseholdSections.FeaturesSchema, ct) is { } features
                ? Results.Ok(features)
                : Results.NotFound());

        household.MapPut(path, async (
            AdminCaller caller, JsonObject? document, HttpContext http, HouseholdConfigStore store, AdminLog log,
            CancellationToken ct) =>
        {
            if (document is null)
            {
                return Results.BadRequest(new AdminRefusal("The features were not saved: send them as a JSON object.", []));
            }

            var before = await store.ReadAsync(caller.HouseholdId, HouseholdSections.FeaturesSchema, ct);
            var merged = AdminSections.Merged(
                WithoutOperatorsFeatures(document), HouseholdSections.FeaturesSchema.Write(before ?? new FeaturesSection()));
            if (merged.Conflicts.Count > 0)
            {
                return AdminSections.Conflict("The features were not saved.", merged, path => $"the {path[0]} feature");
            }

            var kept = merged.Value!.AsObject();
            kept[AppsFeature] = (before ?? new FeaturesSection()).Apps;
            return await AdminSections.AnswerAsync(
                await store.SaveAsync(caller.HouseholdId, HouseholdSections.FeaturesSchema, kept, ct),
                "The features were not saved.",
                async saved =>
                {
                    var after = (await store.ReadAsync(caller.HouseholdId, HouseholdSections.FeaturesSchema, ct))!;
                    await log.RecordAsync(http, caller, Summary(before ?? new FeaturesSection(), after), ct);
                    return Results.Ok(new AdminSaved<FeaturesSection>(after, AdminSections.Warning(saved)));
                });
        });
    }

    // The apps feature is the operator's (it spends the deployment's shared storage and browser), so
    // an owner's save never carries it: taken out of what was sent and of what the page loaded, so it
    // can neither change it nor collide with the operator's change, and put back as it is stored.
    private const string AppsFeature = "apps";

    private static JsonObject WithoutOperatorsFeatures(JsonObject sent)
    {
        sent.Remove(AppsFeature);
        if (sent[AdminEdit.Loaded] is JsonObject loaded)
        {
            loaded.Remove(AppsFeature);
        }

        return sent;
    }

    // Which features changed, by name: a summary never carries a value.
    private static string Summary(FeaturesSection before, FeaturesSection after)
    {
        var changed = AdminLog.Changed(
            ("scheduling", before.Scheduling != after.Scheduling),
            ("timers", before.Timers != after.Timers),
            ("Sugary", before.Sugary != after.Sugary),
            ("Idealista", before.Idealista != after.Idealista),
            ("memory", before.Memory != after.Memory),
            ("workers", before.Workers != after.Workers));
        return changed.Count == 0 ? "Saved the features unchanged" : $"Changed the {string.Join(", ", changed)} features";
    }
}