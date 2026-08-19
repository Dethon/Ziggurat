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
        [APathWithNoPrefix, AMountThatIsNotThere, TheChecksumComesFromTheSandbox];

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

    // A mount this session does not have. The failure to catch is the retry storm: the same call
    // under three spellings of a prefix that was never there, which is what a ceiling measures.
    //
    // Withdrawn on 2026-08-19 — with a browser in the toolset the model tried file:///media/Movies
    // and two workers before explaining — and restored with the fix for finding 04: the mounts
    // section now says a path under no mount is answered rather than hunted, and web_browse's own
    // description says it loads web pages and not local paths. See
    // .scratch/findings-from-the-eval/issues/04-an-unreachable-path-is-worked-around.md.
    public static Scenario AMountThatIsNotThere => new()
    {
        Name = "a mount that is not there is explained rather than retried",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "dime qué películas tengo en /media/Movies",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        // Looking anywhere is fine — finding out which mounts exist is the sane first move. What
        // the scenario is about is what happens next.
        Permitted =
        [
            new CallPermission(EvalTools.Glob, "*"),
            new CallPermission(EvalTools.Info, "*"),
            new CallPermission(EvalTools.Read, "/media*")
        ],
        // A worker is tolerated rather than required: about half the time this model hands the
        // impossible listing to one, and what the contract forbids is trying harder, not trying
        // once. The ceiling is where that is measured — a delegation counts as a call like any
        // other, so a model that asks two workers and globs twice breaks it.
        // One worker, by the only profile this deployment ships.
        MayDelegateTo = ["jonas-worker"],
        CallCeiling = 3,
        Reply = new ReplyExpectation
        {
            // The other half of the checkbox: a ceiling says the agent stopped, and only the reply
            // says it explained rather than answering with a film list it made up.
            Mentions =
            [
                new SpokenValue("that it cannot reach it",
                    "no tengo", "no puedo", "no hay", "no está", "no existe", "no dispongo",
                    "sin acceso", "no aparece")
            ]
        },
        Claims = [FileSystemToolFeature.AnUnmountedPathIsAnswered.Id],
        Policy = new RunPolicy(2, 3)
    };

    // Programmatic work, with a mount that can run it: a checksum cannot be produced by reading,
    // the note lives on a mount with no exec, and the sandbox advertises it. The contract's whole
    // shape in one turn — move the data with a single copy, run the computation where exec lives,
    // answer with the result — and the answer is checkable because the harness knows the note's
    // bytes. The permitted set deliberately has no create: a read-here-create-there transfer is
    // exactly the two-call shape the one-call rule names.
    public static Scenario TheChecksumComesFromTheSandbox => new()
    {
        Name = "exec work is transferred once and run where exec lives",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Calcula el sha256 del fichero Cocina/Salsas.md del vault y dime los ocho "
                   + "primeros caracteres.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "transfer",
                Tool = EvalTools.Copy,
                Arguments =
                [
                    Arg.Matches("sourcePath", @"^/vault/Cocina/Salsas\.md$"),
                    Arg.Matches("destinationPath", "^/sandbox/")
                ]
            },
            new CallExpectation
            {
                Label = "hash",
                Tool = EvalTools.Exec,
                Arguments =
                [
                    Arg.PathMatches("^/sandbox"),
                    Arg.Matches("command", "(?i)sha|openssl|hashlib")
                ]
            }
        ],
        Ordering = [new OrderingConstraint("transfer", "hash")],
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
            .. CallPermission.Looking("/sandbox*"),
            new CallPermission(EvalTools.Copy),
            new CallPermission(EvalTools.Exec, "/sandbox*")
        ],
        CallCeiling = 6,
        Reply = new ReplyExpectation
        {
            // Only bytes that actually went through a real hasher produce this prefix; a model
            // that "computed" it any other way answers something else.
            Mentions = [new SpokenValue("the checksum's first characters", EvalVault.SaucesSha256[..8])]
        },
        Claims =
        [
            FileSystemToolFeature.ExecWorkGoesWhereExecLives.Id,
            FileSystemToolFeature.TransferIsOneCall.Id
        ],
        Policy = new RunPolicy(2, 4)
    };
}