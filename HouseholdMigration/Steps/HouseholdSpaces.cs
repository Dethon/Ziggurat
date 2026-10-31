using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Household;

namespace HouseholdMigration.Steps;

// The chat client's spaces were a list in its own settings (WebChat/appsettings.json, which compose
// never overrode), one list for everybody; a household's spaces are its configuration's now
// (.claude/rules/household-config.md), so the home household is given that list, in its order and
// naming no agent, so each space opens on whatever was picked in it before. Saved through the
// accounts service's seed route, which leaves a section the household already saved as it is: a
// rerun changes nothing and spaces an owner set since are theirs.
public sealed class HouseholdSpaces(HttpClient http, string credential) : IMigrationStep
{
    private const string Path = "accounts/internal/household-config-seed";
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // The list as the chat client last shipped it.
    public static readonly SpacesSection ChatClientsList = new()
    {
        Spaces =
        [
            new HouseholdSpace { Slug = "default", Name = "Main", AccentColor = "#e9601f", Order = 0 },
            new HouseholdSpace { Slug = "x", Name = "X", AccentColor = "#e3c727", Order = 1 }
        ]
    };

    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport("The chat client's spaces into the household");
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(new SeedRequest(
                household.TenantId, HouseholdSections.Spaces, HouseholdSections.SpacesSchema.Write(ChatClientsList), dryRun),
                options: _json)
        };
        request.Headers.Add("Authorization", $"Bearer {credential}");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            report.Problems.Add($"the spaces: {await RefusalAsync(response, ct)}");
            return report;
        }

        var seeded = (await response.Content.ReadFromJsonAsync<Seeded>(_json, ct))!;
        if (seeded.AlreadySaved)
        {
            report.Lines.Add("the household already saved its spaces; they are left as they are");
            return report;
        }

        report.Changes++;
        report.Lines.Add($"{Counted.Of(ChatClientsList.Spaces.Count, "space")}: "
                         + string.Join(", ", ChatClientsList.Spaces.Select(s => $"'{s.Slug}' ({s.Name})")));
        return report;
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

    // The accounts service's seed route (Accounts/Internal/HouseholdConfigSeedApi), spelled again here
    // because that service references no project and nothing references it.
    private sealed record SeedRequest(string HouseholdId, string Section, JsonObject Document, bool DryRun);

    private sealed record Seeded(bool Saved, bool AlreadySaved);

    private sealed record Refused(string Error, IReadOnlyList<string>? Problems);
}