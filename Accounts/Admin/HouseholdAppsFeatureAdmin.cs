using Accounts.Configuration;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;

namespace Accounts.Admin;

// The operator turns a household's apps feature on or off: the one feature its owners cannot change,
// since apps spend the deployment's shared storage and browser (HouseholdFeaturesAdmin keeps it out of
// an owner's save). Saved through the features section's own save, so it is announced and the
// household's agents are offered the apps server, or not, from their next turn.
public static class HouseholdAppsFeatureAdmin
{
    public static void Map(RouteGroupBuilder deployment) =>
        deployment.MapPut(AdminApi.Under(AdminProtocol.Operator, AdminProtocol.AppsFeatureRoute), SetAsync);

    private static async Task<IResult> SetAsync(
        AdminCaller caller, string householdId, HouseholdAppsFeature? request, HttpContext http,
        HouseholdConfigStore store, OneChangeAtATime changes, AdminLog log, CancellationToken ct)
    {
        if (request is null)
        {
            return Results.BadRequest(new AdminRefusal("Nothing was changed: send the apps feature as a JSON object.", []));
        }

        // In the household's turn: an owner's features save reads the section, then saves it whole with
        // apps as it read it, so this change landing in between would be undone.
        return await changes.InTurnAsync(householdId, async () =>
        {
            if (await store.ReadAsync(householdId, HouseholdSections.FeaturesSchema, ct) is not { } before)
            {
                return Results.NotFound(new AdminRefusal("There is no household by that id.", []));
            }

            return await AdminSections.AnswerAsync(
                await store.SaveAsync(
                    householdId, HouseholdSections.FeaturesSchema,
                    HouseholdSections.FeaturesSchema.Write(before with { Apps = request.On }), ct),
                "The apps feature was not changed.",
                async saved =>
                {
                    if (before.Apps != request.On)
                    {
                        await log.RecordAsync(http, caller, householdId,
                            request.On ? "Turned the apps feature on" : "Turned the apps feature off", ct);
                    }

                    return Results.Ok(new AdminSaved<HouseholdAppsFeature>(request, AdminSections.Warning(saved)));
                });
        }, ct);
    }
}