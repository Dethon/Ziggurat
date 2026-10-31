using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HouseholdMigration;

// The accounts service's seed route (Accounts/Internal/HouseholdConfigSeedApi): a section the household
// never saved given a document, laid over what it reads now; one it saved left as it is; a dry run
// validated and written nowhere. Spelled again here because that service references no project and
// nothing references it.
public sealed class HouseholdConfigSeed(HttpClient http, string credential)
{
    private const string Path = "accounts/internal/household-config-seed";
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<Seeding> SeedAsync(
        string householdId, string section, JsonObject document, bool dryRun, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(new SeedRequest(householdId, section, document, dryRun), options: _json)
        };
        request.Headers.Add("Authorization", $"Bearer {credential}");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return new Seeding.Refused(await RefusalAsync(response, ct));
        }

        return (await response.Content.ReadFromJsonAsync<Seeded>(_json, ct))!.AlreadySaved
            ? new Seeding.AlreadySaved()
            : new Seeding.Seeded();
    }

    private static async Task<string> RefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return "the accounts service did not admit the migration (HOUSEHOLD_MIGRATION_SECRET)";
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<Refused>(_json, ct) is { } refused
                ? string.Join("; ", [refused.Error, .. refused.Problems ?? []])
                : $"the accounts service answered {(int)response.StatusCode}";
        }
        catch (JsonException)
        {
            return $"the accounts service answered {(int)response.StatusCode}";
        }
    }

    private sealed record SeedRequest(string HouseholdId, string Section, JsonObject Document, bool DryRun);

    private sealed record Seeded(bool Saved, bool AlreadySaved);

    private sealed record Refused(string Error, IReadOnlyList<string>? Problems);
}

// What became of a seeded section: given it (or in a dry run, would be), left as the household saved
// it, or refused with why.
public abstract record Seeding
{
    public sealed record Seeded : Seeding;

    public sealed record AlreadySaved : Seeding;

    public sealed record Refused(string Why) : Seeding;
}