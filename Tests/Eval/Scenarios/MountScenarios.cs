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
    public static IReadOnlyList<Scenario> All =>
        [APathWithNoPrefix];

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
        Instant = EvalInstant.Evening,
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
        Tier = EvalTier.Smoke,
        // No citation: with the must-start-at-a-mount sentence deleted the model still resolved
        // the note to the vault. The mount list alone is enough to place a path.
        Policy = new RunPolicy(2, 3)
    };
}