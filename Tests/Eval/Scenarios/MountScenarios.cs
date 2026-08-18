using Domain.Tools.FileSystem;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// Where a path belongs, and what happens when the answer is "nowhere". Every mount is a different
// server with a different set of operations, so the failures this family is about are the ones a
// model invents its way around: guessing a prefix, retrying a mount that is not there, or working
// around a capability by hand.
public static class MountScenarios
{
    private static readonly DateTimeOffset _evening =
        new(2026, 8, 17, 20, 0, 0, TimeSpan.FromHours(2));

    public static IReadOnlyList<Scenario> All =>
        [APathWithNoPrefix, AMountThatIsNotThere];

    // A path the user gives without a mount: it exists, under exactly one of them. What the
    // contract asks is that the agent resolves it rather than picking a prefix that reads right.
    public static Scenario APathWithNoPrefix => new()
    {
        Name = "a path with no mount prefix is resolved to its mount",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "léeme lo que dice Cocina/Salsas.md",
            Sender = "fran"
        },
        Instant = _evening,
        Required =
        [
            new CallExpectation
            {
                Label = "read",
                Tool = EvalTools.Read,
                Arguments = [Arg.Path(EvalVault.SaucesNote)]
            }
        ],
        // The unprefixed attempt is tolerated: the contract's own answer to it is an envelope
        // naming the mounts, and acting on that envelope is the behaviour under test.
        Permitted =
        [
            new CallPermission(EvalTools.Glob, "/vault*"),
            new CallPermission(EvalTools.Info, "/vault*"),
            new CallPermission(EvalTools.Search, "/vault*"),
            new CallPermission(EvalTools.Read, "Cocina*"),
            new CallPermission(EvalTools.Info, "Cocina*"),
            new CallPermission(EvalTools.Glob, "*")
        ],
        CallCeiling = 5,
        // No citation: with the must-start-at-a-mount sentence deleted the model still resolved
        // the note to the vault. The mount list alone is enough to place a path.
        Policy = new RunPolicy(2, 3)
    };

    // A mount this session does not have. The failure to catch is the retry storm: the same call
    // under three spellings of a prefix that was never there, which is what a ceiling measures.
    public static Scenario AMountThatIsNotThere => new()
    {
        Name = "a mount that is not there is explained rather than retried",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "dime qué películas tengo en /media/Movies",
            Sender = "fran"
        },
        Instant = _evening,
        Permitted =
        [
            new CallPermission(EvalTools.Glob, "*"),
            new CallPermission(EvalTools.Info, "*"),
            new CallPermission(EvalTools.Read, "/media*")
        ],
        // Three calls is a look, a second look and a stop. A model that keeps trying prefixes
        // breaks this before it answers.
        CallCeiling = 3,
        Claims = [FileSystemToolFeature.AnEnvelopeIsDataNotAReasonToRetry.Id],
        Policy = new RunPolicy(2, 3)
    };
}