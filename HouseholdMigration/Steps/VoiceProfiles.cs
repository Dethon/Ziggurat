namespace HouseholdMigration.Steps;

// A speaker profile is a folder under /voices, and a match on it acts for the account the folder is
// named after (.claude/rules/voice.md); one named after a person, as they were enrolled before
// accounts, is nobody's and its speaker the guest. Each person's folder is renamed to their user id,
// takes and cached prototypes together. The voice channel and the TSE extractor read the folders at
// start, so both are restarted after the run.
public sealed class VoiceProfiles(string voicesPath) : IMigrationStep
{
    public Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport($"Voice profiles in {voicesPath}");
        var enrolled = household.People
            .Where(p => p.Entry.Voice is { Length: > 0 } voice && voice != p.UserId
                        && Directory.Exists(Path.Combine(voicesPath, voice)))
            .ToList();
        foreach (var person in enrolled)
        {
            var from = Path.Combine(voicesPath, person.Entry.Voice!);
            if (person.UserId is not null && Directory.Exists(Path.Combine(voicesPath, person.UserId)))
            {
                report.Problems.Add(
                    $"{person.Label} has a profile under their user id already, so '{person.Entry.Voice}' was not renamed");
                continue;
            }

            if (!dryRun)
            {
                Directory.Move(from, Path.Combine(voicesPath, person.UserId!));
            }

            report.Changes++;
            report.Lines.Add($"{person.Label}: '{person.Entry.Voice}' renamed");
        }

        return Task.FromResult(report);
    }
}