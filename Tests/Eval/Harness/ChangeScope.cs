using Domain.Prompts;

namespace Tests.Eval.Harness;

// Which scenarios a diff is worth running. A claim is declared beside the prose that teaches it
// (ADR-0031), so the file that spells a claim's id is the file whose edit can break the claim,
// and every scenario that tests the claim — citing, guarding, judging, or conditionally — is
// the diff's. A scenario file that changed runs whole: its scenarios are what changed.
//
// Nothing else selects. A tool description, a fixture, a harness edit or the agent definition
// have no claims to be read through, and a tier that guessed at them would be a full pass that
// sometimes was not. What the changed tier answers is "did this prompt edit break what it
// teaches", and its scorecard is filed under its own name so it is never read as more.
public static class ChangeScope
{
    private const string ScenarioFiles = "Tests/Eval/Scenarios/";
    private const string TestFiles = "Tests/";

    public static IReadOnlyList<Scenario> Select(
        IReadOnlyList<string> changed, Func<string, string?> read,
        IReadOnlyList<Scenario> suite, IReadOnlyList<PromptClaim> claims)
    {
        var contents = changed
            .Select(path => (Path: Normalised(path), Content: read(Normalised(path))))
            .Where(file => file.Content is not null)
            .ToList();

        // Only the prompt's own files declare: the ledger tests and the dumps spell ids too.
        var declared = contents
            .Where(file => !file.Path.StartsWith(TestFiles, StringComparison.Ordinal))
            .SelectMany(file => claims.Where(claim => file.Content!.Contains($"\"{claim.Id}\"")))
            .Select(claim => claim.Id)
            .ToHashSet();

        var named = contents
            .Where(file => file.Path.StartsWith(ScenarioFiles, StringComparison.Ordinal))
            .ToList();

        return [.. suite.Where(scenario =>
            Tests(scenario).Any(declared.Contains)
            || named.Any(file => file.Content!.Contains($"\"{scenario.Name}\"")))];
    }

    // Every claim a scenario is evidence about, the way the coverage test counts them.
    private static IEnumerable<string> Tests(Scenario scenario) =>
        scenario.Claims
            .Concat(scenario.Guards.Select(guard => guard.Claim))
            .Concat(scenario.Judged.Select(check => check.Claim))
            .Concat(scenario.IfDelegated.SelectMany(condition =>
                condition.Claims.Concat(condition.Judged.Select(check => check.Claim))));

    private static string Normalised(string path) => path.Replace('\\', '/');
}