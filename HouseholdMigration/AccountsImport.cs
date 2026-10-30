using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HouseholdMigration;

// The accounts service's household import (Accounts/Internal/HouseholdImportApi), spelled again here
// because that service references no project and nothing references it.
internal sealed record ImportRequest(IReadOnlyList<ImportPerson> People, bool DryRun);

internal sealed record ImportPerson(
    string Key, string DisplayName, long? TelegramId, string Role, bool Operator, string? AvatarUrl);

internal sealed record Imported(string TenantId, IReadOnlyList<ImportedPerson> People);

internal sealed record ImportedPerson(
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

internal sealed record ImportRefused(string Error);

// The people made accounts of the home household, first and before anything is moved: every other
// step files things under the user ids this answers with. A passkey link is printed for each person
// the run created, since it exists in clear only in this answer.
public sealed class AccountsImport(HttpClient http, string credential)
{
    private const string Path = "accounts/internal/household-import";
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<(Household Household, StepReport Report)> ImportAsync(
        PeopleFile file, string operatorName, string ownerName, string sugaryName, bool dryRun, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(new ImportRequest(
                [
                    .. file.People.Select(p => new ImportPerson(
                        p.Name, p.ShownAs, p.TelegramId,
                        p.Name == operatorName || p.Name == ownerName ? "owner" : p.Role ?? "member",
                        p.Name == operatorName,
                        p.AvatarUrl))
                ],
                dryRun), options: _json)
        };
        request.Headers.Add("Authorization", $"Bearer {credential}");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new MigrationRefusedException(await RefusalAsync(response, ct));
        }

        var imported = (await response.Content.ReadFromJsonAsync<Imported>(_json, ct))!;
        var people = file.People
            .Select(entry => (Entry: entry, Account: imported.People.Single(p => p.Key == entry.Name)))
            .ToList();
        var migrated = people
            .Select(p => new MigratedPerson(p.Entry, p.Account.UserId, imported.TenantId, p.Account.DisplayName))
            .ToList();
        var report = new StepReport($"People of the household {imported.TenantId}");
        report.Changes = people.Sum(p => p.Account.Changes.Count);
        report.Lines.AddRange(people.SelectMany(p => Describe(p.Account, migrated.Single(m => m.Entry == p.Entry))));
        return (new Household(
            imported.TenantId, migrated,
            migrated.Single(m => m.Entry.Name == ownerName),
            migrated.Single(m => m.Entry.Name == sugaryName)), report);
    }

    private static IEnumerable<string> Describe(ImportedPerson account, MigratedPerson person)
    {
        var what = $"{account.Role}{(account.Operator ? ", the operator" : "")}";
        yield return account.Changes.Count == 0
            ? $"{person.Label}, {what}: already there"
            : $"{person.Label}, {what}: {string.Join(", ", account.Changes)}";
        foreach (var note in account.Notes)
        {
            yield return $"  {note}";
        }

        if (account.Link is { } link)
        {
            yield return $"  their passkey link, good once until {account.LinkExpiresAt!.Value.UtcDateTime:yyyy-MM-dd HH:mm} UTC:";
            yield return $"  {link}";
        }
        else if (account is { HasPasskey: false, UserId: { } userId })
        {
            yield return $"  no passkey yet: docker exec accounts dotnet Accounts.dll mint-link --user {userId}";
        }
    }

    private static async Task<string> RefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return "the accounts service did not admit the migration: HOUSEHOLD_MIGRATION_SECRET in .env must be "
                   + "set, and accounts recreated since (docker compose up -d accounts), so its "
                   + "CALLERS__household-migration is the same value";
        }

        try
        {
            return (await response.Content.ReadFromJsonAsync<ImportRefused>(_json, ct))?.Error
                   ?? $"the accounts service answered {(int)response.StatusCode}";
        }
        catch (JsonException)
        {
            return $"the accounts service answered {(int)response.StatusCode}";
        }
    }
}