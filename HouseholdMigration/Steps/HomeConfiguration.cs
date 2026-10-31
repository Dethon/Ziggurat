using System.Text.Json.Nodes;
using Domain.DTOs.Household;

namespace HouseholdMigration.Steps;

// The home household's own configuration, from what the deployment ran before households had one
// (.claude/rules/household-config.md): its settings with the deployment's zone, its agents — jack,
// jonas, nabu and sugary, the definitions the agent's settings carried, seeded whole — and its
// features, every one its agents used on. Each through the accounts service's seed route, which lays
// the document over what the household reads now (so the settings keep the name its row carries) and
// leaves a section the household already saved as it is: a rerun changes nothing, and what the
// operator or an owner set since is theirs.
//
// The agents and the features are files beside this step (Home/), embedded, because they are what the
// repo says the home ran: the smoke stack seeds the same two files, and Tests/TestAgents.json is held
// equal to the agents.
public sealed class HomeConfiguration(HouseholdConfigSeed seed, string timeZone) : IMigrationStep
{
    public static JsonObject Agents => Embedded("agents.json");

    public static JsonObject Features => Embedded("features.json");

    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport("The home household's settings, agents and features");
        var agents = HouseholdSections.AgentsSchema.Read(HouseholdSections.AgentsSchema.Migrate(Agents));

        // No language: jack and jonas follow the conversation, and nabu pins its own.
        await SeedAsync(report, household, HouseholdSections.Settings,
            new JsonObject { ["timeZone"] = timeZone, ["language"] = null },
            $"its days read in {timeZone}, the deployment's zone, and no language of its own", dryRun, ct);
        await SeedAsync(report, household, HouseholdSections.Agents, Agents,
            $"{Counted.Of(agents.Agents.Count, "agent")}: {string.Join(", ", agents.Agents.Select(a => a.Id))}, "
            + $"{agents.DefaultAgent} answering where a channel names none", dryRun, ct);
        await SeedAsync(report, household, HouseholdSections.Features, Features,
            "every feature on, Idealista too", dryRun, ct);
        return report;
    }

    private async Task SeedAsync(
        StepReport report, Household household, string section, JsonObject document, string what, bool dryRun,
        CancellationToken ct)
    {
        switch (await seed.SeedAsync(household.TenantId, section, document, dryRun, ct))
        {
            case Seeding.Refused refused:
                report.Problems.Add($"the {section}: {refused.Why}");
                break;
            case Seeding.AlreadySaved:
                report.Lines.Add($"the household already saved its {section}; they are left as they are");
                break;
            default:
                report.Changes++;
                report.Lines.Add($"{section}: {what}");
                break;
        }
    }

    private static JsonObject Embedded(string name)
    {
        using var stream = typeof(HomeConfiguration).Assembly.GetManifestResourceStream($"HouseholdMigration.Home.{name}")
                           ?? throw new InvalidOperationException($"HouseholdMigration/Home/{name} was not embedded");
        return JsonNode.Parse(stream)!.AsObject();
    }
}