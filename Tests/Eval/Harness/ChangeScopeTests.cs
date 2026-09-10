using Domain.Prompts;
using Shouldly;

namespace Tests.Eval.Harness;

// Which scenarios a diff is worth running: the ones whose claims are declared in a file that
// changed, and the ones whose own file did. A claim is declared beside the prose that teaches it
// (ADR-0031), so the file that spells a claim's id is the file whose edit can break it.
public class ChangeScopeTests
{
    private static readonly PromptClaim _timer = new("timers.duration-is-a-countdown", "a timer");
    private static readonly PromptClaim _vault = new("vault.edits-are-surgical", "a vault");
    private static readonly PromptClaim _delegate = new("subagents.prompt-is-self-contained", "a worker");

    private static readonly Scenario _cites = Synthetic("cites a timer", claims: [_timer.Id]);
    private static readonly Scenario _guards = Synthetic("guards a timer", guards: [_timer.Id]);
    private static readonly Scenario _judges = Synthetic("judges a timer", judged: [_timer.Id]);
    private static readonly Scenario _conditional = Synthetic("delegates", conditional: [_delegate.Id]);
    private static readonly Scenario _other = Synthetic("edits a note", claims: [_vault.Id]);

    private static readonly IReadOnlyList<Scenario> _suite =
        [_cites, _guards, _judges, _conditional, _other];

    private static readonly IReadOnlyList<PromptClaim> _claims = [_timer, _vault, _delegate];

    [Fact]
    public void AChangedFileThatDeclaresAClaim_SelectsEveryScenarioThatTestsIt()
    {
        var selected = ChangeScope.Select(
            ["Domain/Prompts/TimerPrompt.cs"],
            Reading(("Domain/Prompts/TimerPrompt.cs", $"new(\"{_timer.Id}\", \"...\")")),
            _suite, _claims);

        selected.Select(s => s.Name).ShouldBe(
            ["cites a timer", "guards a timer", "judges a timer"], ignoreOrder: true);
    }

    [Fact]
    public void AConditionalCitation_CountsAsACitation()
    {
        var selected = ChangeScope.Select(
            ["Domain/Prompts/SubAgentPrompt.cs"],
            Reading(("Domain/Prompts/SubAgentPrompt.cs", $"\"{_delegate.Id}\"")),
            _suite, _claims);

        selected.ShouldHaveSingleItem().Name.ShouldBe("delegates");
    }

    [Fact]
    public void AChangedScenarioFile_SelectsEveryScenarioItNames()
    {
        var selected = ChangeScope.Select(
            ["Tests/Eval/Scenarios/VaultScenarios.cs"],
            Reading(("Tests/Eval/Scenarios/VaultScenarios.cs",
                "Name = \"edits a note\",\n ... Name = \"guards a timer\",")),
            _suite, _claims);

        selected.Select(s => s.Name).ShouldBe(["edits a note", "guards a timer"], ignoreOrder: true);
    }

    [Fact]
    public void AClaimSpelledInATestFile_IsNotADeclaration()
    {
        // The ledger tests and the dumps spell claim ids too; only the prompt's own files
        // declare them, and a harness edit is not a prompt edit.
        var selected = ChangeScope.Select(
            ["Tests/Eval/ScorecardLedgerTests.cs"],
            Reading(("Tests/Eval/ScorecardLedgerTests.cs", $"\"{_timer.Id}\"")),
            _suite, _claims);

        selected.ShouldBeEmpty();
    }

    [Fact]
    public void AnUnrelatedChange_SelectsNothing()
    {
        var selected = ChangeScope.Select(
            ["Infrastructure/Agents/McpAgent.cs", "docs/adr/0040-a-scenario-stops-at-its-threshold.md"],
            Reading(("Infrastructure/Agents/McpAgent.cs", "class McpAgent {}")),
            _suite, _claims);

        selected.ShouldBeEmpty();
    }

    [Fact]
    public void AFileThatCannotBeRead_SelectsNothing_RatherThanThrowing()
    {
        // Deleted in the working tree, which the diff still lists.
        var selected = ChangeScope.Select(
            ["Domain/Prompts/Gone.cs"], _ => null, _suite, _claims);

        selected.ShouldBeEmpty();
    }

    [Fact]
    public void AgainstTheRealSuite_TheTimerPromptSelectsItsOwnFamilyAndNoVaultScenario()
    {
        // The seam this tier stands on: a real section file, the real manifest, the real suite.
        var selected = ChangeScope.Select(
            ["Domain/Prompts/TimerPrompt.cs"],
            path => File.ReadAllText(Path.Combine(RepositoryRoot.Path, path)),
            EvalSuite.All, PromptManifest.Claims);

        selected.ShouldNotBeEmpty();
        selected.ShouldAllBe(s => Cited(s).Any(claim => claim.StartsWith("timers.")));
        selected.ShouldNotContain(s => Cited(s).Any(claim => claim.StartsWith("vault.")));
    }

    private static IEnumerable<string> Cited(Scenario scenario) =>
        scenario.Claims
            .Concat(scenario.Guards.Select(g => g.Claim))
            .Concat(scenario.Judged.Select(j => j.Claim));

    private static Func<string, string?> Reading(params (string Path, string Content)[] files) =>
        path => files.FirstOrDefault(file => file.Path == path).Content;

    private static Scenario Synthetic(
        string name, IReadOnlyList<string>? claims = null, IReadOnlyList<string>? guards = null,
        IReadOnlyList<string>? judged = null, IReadOnlyList<string>? conditional = null) => new()
        {
            Name = name,
            AgentId = "nabu",
            Turn = new EvalTurn { Text = "hola", Sender = "fran" },
            Instant = EvalInstant.Evening,
            CallCeiling = 1,
            Claims = claims ?? [],
            Guards = [.. (guards ?? []).Select(claim => new Guard(claim, "note"))],
            Judged = [.. (judged ?? []).Select(claim => new JudgedCheck(claim, "rubric"))],
            IfDelegated = conditional is null
                ? []
                : [new ConditionalDelegation { Profile = "research", Claims = conditional }],
            MayDelegateTo = conditional is null ? [] : ["research"]
        };
}