using Accounts.People;
using Accounts.Sessions;
using Accounts.Settings;
using Accounts.Store;

namespace Accounts.Internal;

public sealed record HouseholdImportRequest(IReadOnlyList<HouseholdImportPerson> People, bool DryRun);

public sealed record HouseholdImportPerson(string Key, string DisplayName, long? TelegramId, string Role, bool Operator);

public sealed record HouseholdImported(string TenantId, IReadOnlyList<HouseholdImportedPerson> People);

public sealed record HouseholdImportedPerson(
    string Key,
    string? UserId,
    string DisplayName,
    string Role,
    bool Operator,
    bool Created,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Notes,
    bool HasPasskey,
    Uri? Link,
    DateTimeOffset? LinkExpiresAt);

public sealed record HouseholdImportRefused(string Error);

// The household migration's one call: the identity map's people made accounts of the home household
// (HouseholdImporter). It can make the operator and mint their passkey link, so unlike the rest of the
// internal API it answers only the migration's own credential (CALLERS__household-migration), which
// the deployment leaves empty — nobody — except for the run.
public static class HouseholdImportApi
{
    public const string Path = "/household-import";
    public const string Caller = "household-migration";

    public static void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(Path, async (
            HttpContext http, HouseholdImportRequest request, HouseholdImporter importer, AccountsSettings settings,
            CancellationToken ct) =>
        {
            if (!settings.Callers.TryGetValue(Caller, out var credential)
                || !CallerCredential.Matches(http.Request.Headers.Authorization.ToString(), credential))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (request.People.FirstOrDefault(p => RoleNamed(p.Role) is null) is { } unknown)
            {
                return Results.BadRequest(new HouseholdImportRefused(
                    $"{unknown.Key}'s role is '{unknown.Role}', which is neither {Roles.Owner} nor {Roles.Member}"));
            }

            var people = request.People
                .Select(p => new ImportingPerson(p.Key, p.DisplayName, p.TelegramId, RoleNamed(p.Role)!.Value, p.Operator))
                .ToList();
            return await importer.ImportAsync(people, request.DryRun, ct) switch
            {
                HouseholdImport.Imported imported => Results.Ok(new HouseholdImported(
                    imported.TenantId,
                    [.. imported.People.Select(p => new HouseholdImportedPerson(
                        p.Key, p.UserId, p.DisplayName, p.Role == Role.Owner ? Roles.Owner : Roles.Member, p.Operator,
                        p.Created, p.Changes, p.Notes, p.HasPasskey, p.Link?.Url, p.Link?.ExpiresAt))])),
                HouseholdImport.Conflict conflict => Results.Conflict(new HouseholdImportRefused(conflict.Reason)),
                HouseholdImport.Invalid invalid => Results.BadRequest(new HouseholdImportRefused(invalid.Reason)),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
            };
        });

    private static Role? RoleNamed(string role) => role switch
    {
        Roles.Owner => Role.Owner,
        Roles.Member => Role.Member,
        _ => null
    };
}