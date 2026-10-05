using Domain.DTOs.Voice;
using Domain.Prompts;
using Domain.Tools.FileSystem;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// Every mount is a filesystem to the shell: inside a sandbox command the session's other mounts
// are directories at their own paths, actions run from scripts, and what a command changed through
// them is in the result's vfsChanges. What this family measures is whether the model uses that
// route where it is the right one — and reads what it reports — or ignores it.
public static class ShellScenarios
{
    public static IReadOnlyList<Scenario> All =>
    [
        WordsAcrossTheVault, ADismissInsideAScript, AWriteTheVaultRefuses, AMoveInsideTheVault, AMachineIsNotInTheSandbox
    ];

    // An aggregate no single tool answers: the word count of a folder. The notes are read in place
    // at /vault from a command, with nothing copied into the sandbox first — and the number is one
    // only a real count over the real bytes produces, under whichever definition of a word the
    // script chose.
    public static Scenario WordsAcrossTheVault => new()
    {
        Name = "a count over vault notes runs on /vault in place",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "¿Cuántas palabras suman en total las notas de la carpeta Cocina del vault?",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            CallExpectation.LoadsSkill(SandboxSkill.Name),
            new CallExpectation
            {
                Label = "count",
                Tool = EvalTools.Exec,
                Arguments = [Arg.Matches("command", "(?i)wc|split\\(|len\\("), Arg.Matches("command", "Cocina")]
            }
        ],
        Ordering = [new OrderingConstraint("skill", "count")],
        // Looking at the folder, or at a note to check the count, is fine — the number in the reply
        // is what tells a real count from one done by eye. A copy into the sandbox is the route this
        // family exists to retire, and is not permitted.
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
            new CallPermission(EvalTools.Exec, "/sandbox*"),
            new CallPermission(EvalTools.Exec, "/vault*"),
            CallPermission.Load(ObsidianVaultSkill.Name)
        ],
        CallCeiling = 5,
        Reply = new ReplyExpectation
        {
            Mentions =
            [
                new SpokenValue("the folder's word count",
                    [.. EvalVault.CocinaWordCounts.Select(count => count.ToString())])
            ]
        },
        Claims = [SandboxSkill.WorksOnTheMountsInPlace.Id, VaultPrompt.ScriptsRunInPlace.Id],
        Policy = new RunPolicy(2, 3)
    };

    // One command that silences what is ringing and then looks at what is left: the action runs
    // from the script, with its own output, rather than as a separate exec beside it.
    public static Scenario ADismissInsideAScript => new()
    {
        Name = "an action runs from inside a script",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Con un único comando en el sandbox: para la alarma que está sonando y después "
                   + "lista los temporizadores que siguen en marcha.",
            Sender = "fran",
            Ringing = new DismissedAlert("Sacar la basura", AnnounceKind.Alarm)
        },
        Instant = EvalInstant.Evening,
        Armed =
        [
            new ArmedTimerSeed("pasta", DurationSeconds: 480, Room: "kitchen", RunningFor: TimeSpan.FromMinutes(3)),
            new ArmedTimerSeed("te", DurationSeconds: 300, Room: "office", RunningFor: TimeSpan.FromMinutes(1))
        ],
        Required =
        [
            new CallExpectation
            {
                Label = "script",
                Tool = EvalTools.Exec,
                Arguments = [Arg.Matches("command", "dismiss"), Arg.Matches("command", "(?s)dismiss.*(ls|find|cat|jq|status\\.json|/timers)")]
            }
        ],
        Permitted =
        [
            .. CallPermission.Looking("/timers*"),
            new CallPermission(EvalTools.Exec, "/sandbox*"),
            new CallPermission(EvalTools.Exec, "/timers*"),
            CallPermission.Load(SandboxSkill.Name),
            CallPermission.Load(CountdownTimersSkill.Name)
        ],
        CallCeiling = 5,
        Reply = new ReplyExpectation
        {
            Mentions =
            [
                new SpokenValue("the pasta timer", "pasta"),
                new SpokenValue("the tea timer", "te", "té")
            ]
        },
        Claims = [SandboxSkill.ActionsRunFromScripts.Id],
        Policy = new RunPolicy(2, 3)
    };

    // The vault authors only some extensions as text, and a command's write is the text tool's: a
    // CSV is refused. Bash exits 0 all the same — it swallows the refusal at close — so the only
    // honest report is the one vfsChanges makes, and the file is not there.
    public static Scenario AWriteTheVaultRefuses => new()
    {
        Name = "a refused write is reported from vfsChanges",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Con un comando del sandbox, guarda en el vault el fichero Proyectos/palabras.csv con "
                   + "el nombre de cada nota de Proyectos y su número de palabras.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "write",
                Tool = EvalTools.Exec,
                Arguments = [Arg.Matches("command", "palabras\\.csv")]
            }
        ],
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
            new CallPermission(EvalTools.Exec, "/sandbox*"),
            new CallPermission(EvalTools.Exec, "/vault*"),
            CallPermission.Load(SandboxSkill.Name),
            CallPermission.Load(ObsidianVaultSkill.Name)
        ],
        // Looking at the notes, counting, writing, and the write again in a format the vault takes
        // once the refusal is read: demonstrated on 2026-10-01, seven calls, and the right ones.
        CallCeiling = 8,
        Files = [new FileExpectation { Path = $"{EvalVault.Mount}/Proyectos/palabras.csv", Deleted = true }],
        Reply = new ReplyExpectation
        {
            Mentions =
            [
                new SpokenValue("that the CSV was not saved",
                    "no se pudo", "no he podido", "no ha podido", "no puedo", "no se puede", "rechaz",
                    "no lo permite", "no permite", "no admite", "solo admite", "sólo admite", "no acepta",
                    "extensión", "extensiones", "no se guard",
                    "no se ha guardado", "no se creó", "no se ha creado", "denegad", "refused")
            ]
        },
        Claims = [SandboxSkill.ReportsWhatVfsChangesSays.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A batch move inside the vault, from one command: the vault's own move, so its rules apply,
    // and the notes end up where they were asked to go.
    //
    // The spec asks for a move between two mounts; the agent this suite runs has no second mount a
    // note belongs on (timers, schedules and the home take no notes), so the transfer half is
    // pinned by the bridge's own tests and this one asks the shell's mv on the one mount it fits.
    public static Scenario AMoveInsideTheVault => new()
    {
        Name = "an mv over vault notes is the vault's own move",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Con un comando del sandbox, mueve las notas «Viaje a Japón» y «Curso de alemán» "
                   + "de Proyectos a una carpeta nueva Proyectos/Aprendizaje.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Required =
        [
            new CallExpectation
            {
                Label = "move",
                Tool = EvalTools.Exec,
                Arguments = [Arg.Matches("command", "\\bmv\\b")]
            }
        ],
        Permitted =
        [
            .. CallPermission.Looking("/vault*"),
            new CallPermission(EvalTools.Exec, "/sandbox*"),
            new CallPermission(EvalTools.Exec, "/vault*"),
            CallPermission.Load(SandboxSkill.Name),
            CallPermission.Load(ObsidianVaultSkill.Name)
        ],
        CallCeiling = 6,
        Files =
        [
            new FileExpectation { Path = $"{EvalVault.Mount}/Proyectos/Aprendizaje/Viaje a Japón.md", Contains = ["# Viaje a Japón"] },
            new FileExpectation { Path = $"{EvalVault.Mount}/Proyectos/Aprendizaje/Curso de alemán.md", Contains = ["# Curso de alemán"] },
            new FileExpectation { Path = $"{EvalVault.Mount}/Proyectos/Viaje a Japón.md", Deleted = true },
            new FileExpectation { Path = $"{EvalVault.Mount}/Proyectos/Curso de alemán.md", Deleted = true }
        ],
        Claims = [SandboxSkill.WorksOnTheMountsInPlace.Id],
        Policy = new RunPolicy(2, 3)
    };

    // A machine is somebody else's computer and is never inside a command. Asked what is on one
    // that is not connected, the agent asks the file tools at the machine's own address — which say
    // it is not here — and never goes looking for it in the sandbox's tree. (Told explicitly to look
    // from the sandbox, one look spelled as given is the sandbox prompt's own allowance, so the
    // request does not name the sandbox.)
    public static Scenario AMachineIsNotInTheSandbox => new()
    {
        Name = "a machine's path is never looked for from the sandbox",
        AgentId = "jonas",
        Turn = new EvalTurn
        {
            Text = "Enséñame qué hay en outpost:portatil/home/fran.",
            Sender = "fran"
        },
        Instant = EvalInstant.Evening,
        Permitted =
        [
            new CallPermission(EvalTools.Glob, "*"),
            new CallPermission(EvalTools.Info, "*"),
            new CallPermission(EvalTools.Read, "outpost:*")
        ],
        CallCeiling = 3,
        Reply = new ReplyExpectation
        {
            Mentions =
            [
                new SpokenValue("that it cannot reach it",
                    "no tengo", "no puedo", "no hay", "no está", "no existe", "no dispongo", "sin acceso",
                    "no aparece", "no es accesible", "inaccesible", "no accesible", "no conectad", "no encuentro")
            ]
        },
        Claims = [FileSystemToolFeature.AnOutpostIsNeverInTheSandbox.Id],
        Policy = new RunPolicy(2, 3)
    };
}