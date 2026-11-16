using Domain.DTOs.Household;

namespace HouseholdMigration.Steps;

// The chat client's spaces were a list in its own settings (WebChat/appsettings.json, which compose
// never overrode), one list for everybody; a household's spaces are its configuration's now
// (.claude/rules/household-config.md), so the home household is given that list, in its order and
// naming no agent, so each space opens on whatever was picked in it before. Saved through the
// accounts service's seed route, which leaves a section the household already saved as it is: a
// rerun changes nothing and spaces an owner set since are theirs.
public sealed class HouseholdSpaces(HouseholdConfigSeed seed) : IMigrationStep
{
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
        switch (await seed.SeedAsync(
                    household.TenantId, HouseholdSections.Spaces, HouseholdSections.SpacesSchema.Write(ChatClientsList), dryRun, ct))
        {
            case Seeding.Refused refused:
                report.Problems.Add($"the spaces: {refused.Why}");
                break;
            case Seeding.AlreadySaved:
                report.Lines.Add("the household already saved its spaces; they are left as they are");
                break;
            default:
                report.Changes++;
                report.Lines.Add($"{Counted.Of(ChatClientsList.Spaces.Count, "space")}: "
                                 + string.Join(", ", ChatClientsList.Spaces.Select(s => $"'{s.Slug}' ({s.Name})")));
                break;
        }

        return report;
    }
}