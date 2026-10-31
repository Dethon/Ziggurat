using System.Text.Json.Nodes;
using Accounts.Configuration;
using Accounts.Settings;
using Domain.DTOs.Household;

namespace Accounts.Internal;

public sealed record HouseholdConfigSeedRequest(string HouseholdId, string Section, JsonObject Document, bool DryRun);

// Saved: the document became the section. AlreadySaved: the household had saved the section, which
// was left as it is. A dry run answers what it would have done and writes nothing.
public sealed record HouseholdConfigSeeded(bool Saved, bool AlreadySaved);

public sealed record HouseholdConfigSeedRefused(string Error, IReadOnlyList<string>? Problems = null);

// The household migration's way to give a household a section it never saved, from what the
// deployment kept before households had configurations (the chat client's spaces, the agents'
// definitions). A section the household already saved is left as it is, so a rerun changes nothing
// and nothing an owner set since is put over. Like the import, only the migration's own credential
// is admitted (CALLERS__household-migration), which the deployment leaves empty but for the run.
//
// The document is laid over the section as the household reads it now — its defaults — field by
// top-level field, so what the migration does not know stays the household's: its settings' name is
// the one its row carries, and a features document naming one feature leaves the rest as they were.
public static class HouseholdConfigSeedApi
{
    public const string Path = "/household-config-seed";

    public static void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(Path, async (
            HttpContext http, HouseholdConfigSeedRequest request, HouseholdConfigStore store, HouseholdDataKeys dataKeys,
            AccountsSettings settings, CancellationToken ct) =>
        {
            if (!settings.Callers.TryGetValue(HouseholdImportApi.Caller, out var credential)
                || !CallerCredential.Matches(http.Request.Headers.Authorization.ToString(), credential))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (HouseholdSections.Named(request.Section) is not { } schema)
            {
                return Results.BadRequest(new HouseholdConfigSeedRefused($"there is no section '{request.Section}'"));
            }

            if (await store.HouseholdAsync(request.HouseholdId, ct) is null)
            {
                return Results.NotFound(new HouseholdConfigSeedRefused($"there is no household {request.HouseholdId}"));
            }

            if ((await store.HouseholdsWithAsync(schema.Name, ct)).Contains(request.HouseholdId))
            {
                return Results.Ok(new HouseholdConfigSeeded(Saved: false, AlreadySaved: true));
            }

            JsonObject document;
            try
            {
                document = Over(await store.ReadAsync(request.HouseholdId, schema, ct) ?? schema.Default,
                    schema.Migrate((JsonObject)request.Document.DeepClone()));
            }
            catch (SectionShapeException ex)
            {
                return Results.BadRequest(new HouseholdConfigSeedRefused($"the {schema.Name} would not be saved", [ex.Message]));
            }

            // A stack with no master key seals nothing, so a seeded secret is refused as the real run would be.
            if (dataKeys.MasterKeyId is null && SealedFields.HasSecret(document, schema.SecretPaths))
            {
                return SecretsUnavailable(schema, "no master key is configured, so no household secret can be sealed");
            }

            if (request.DryRun)
            {
                var problems = Problems(schema, document);
                return problems.Count > 0
                    ? Results.BadRequest(new HouseholdConfigSeedRefused($"the {schema.Name} would not be saved", problems))
                    : Results.Ok(new HouseholdConfigSeeded(Saved: false, AlreadySaved: false));
            }

            SectionSave saved;
            try
            {
                saved = await store.SaveAsync(request.HouseholdId, schema, document, ct);
            }
            catch (HouseholdSecretsUnavailableException ex)
            {
                return SecretsUnavailable(schema, ex.Message);
            }

            return saved switch
            {
                SectionSave.Saved => Results.Ok(new HouseholdConfigSeeded(Saved: true, AlreadySaved: false)),
                SectionSave.Invalid invalid => Results.BadRequest(
                    new HouseholdConfigSeedRefused($"the {schema.Name} was not saved", invalid.Problems)),
                _ => Results.NotFound(new HouseholdConfigSeedRefused($"there is no household {request.HouseholdId}"))
            };
        });

    private static IResult SecretsUnavailable(SectionSchema schema, string why) =>
        Results.Json(new HouseholdConfigSeedRefused($"the {schema.Name} would not be saved", [why]),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static JsonObject Over(JsonObject current, JsonObject seeded)
    {
        foreach (var (field, value) in seeded)
        {
            current[field] = value?.DeepClone();
        }

        return current;
    }

    // What a save would refuse, without saving: a seeded document carries no secret marker to keep.
    private static IReadOnlyList<string> Problems(SectionSchema schema, JsonObject document)
    {
        try
        {
            return schema.Problems(schema.Migrate((JsonObject)document.DeepClone()));
        }
        catch (SectionShapeException ex)
        {
            return [ex.Message];
        }
    }
}