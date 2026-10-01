using Domain.Contracts;

namespace Domain.Prompts;

// The sandbox's doing rules, loaded when a request runs something. The section had no choosing
// rule of its own — that the sandbox is the one mount with exec is in the mounts list and in the
// stub — so it moved whole (docs/adr/0039). Built from the two values the mount declares about
// itself, as the prompt was, so a rename of the mount point cannot leave the prose quietly wrong.
public static class SandboxSkill
{
    public const string Name = "sandbox";

    // The whole trigger: the one line about this skill that is in every turn.
    public const string Description =
        "Running anything in the Linux sandbox — a command, a script, a checksum, a pip install, a git clone, a count or a rewrite over another mount's files (\"compute the sha256 of that file\", \"run this python\", \"count the words in those notes\"). Not for reading or editing one file, which the file tools do. The layout, what persists, what is preinstalled, how exit codes, output caps and timeouts come back, how the other mounts appear inside a command, and how a container path maps to a virtual one.";

    // `servesMounts`, as for the prompt: the other-mounts section is taught only where a command
    // will find them.
    public static string Body(string mountPoint, string workspace, bool servesMounts = false)
    {
        var home = FileSystemResolution.ToVirtualPath(mountPoint, workspace);
        // The same directory as the container spells it, which is what command output will show.
        var native = "/" + workspace.TrimStart('/');

        return $"""
            ### Layout

            - `{mountPoint}` — the container root (`/`), for every tool including command execution. The container knows this name too, so `{mountPoint}/etc/os-release` and `/etc/os-release` are the same file whether you write one as a path argument or inside a command.
            - `{home}` — the **persistent workspace** (a Docker named volume). Files here survive container restarts. Nothing puts you here by default: name it as the working directory when you want to work in it.
            - `{mountPoint}/etc`, `{mountPoint}/usr`, `{mountPoint}/tmp`, etc. — system directories. They reset whenever the container is recreated and you typically cannot write to them (you run as an unprivileged user) — the container root included, so a command that writes a relative file needs a working directory you own.

            ### Capabilities

            - **File operations.** Standard read/write/glob/search/move/remove are all available. Scope them deliberately: this mount's root is the whole container, so a recursive glob or search starting at `{mountPoint}` is a walk over every path in the image. Both stop at a budget and say so, but the answer you get back covers whatever they reached before stopping. Start from the directory you mean — `{home}` for your own files.
            - **Command execution.** Commands run via `bash -lc` inside the container. Each call is a fresh shell — environment variables and `cd` do **not** persist between calls; files written to the persistent workspace do. `path` is the working directory as a virtual path (`{mountPoint}` is the container root; name `{home}` to work in the workspace), and `timeoutSeconds` is clamped to the backend's maximum.
            - **Preinstalled tooling.** `bash`, `python3` + `pip` + `venv`, `git`, `curl`, `jq`, `unzip`, plus the standard coreutils. Install extra Python packages with `pip install --user <package>` (user-scope; persists in your home).
            - **Network.** Full **outbound** network is available (you can `curl`, `git clone`, `pip install`). The sandbox does **not** publish inbound ports — external clients cannot reach a server you start inside it.

            ### Behaviour you should rely on

            - **Exit codes** are returned as data, not raised as errors — branch on them.
            - **Output is capped** per stream and the result flags truncation; for long output, redirect to a file and read it back with the file tools.
            - **Timeouts** kill the entire process tree and surface a timeout flag; raise the limit only when you genuinely need a longer-running command.
            - **The persistent workspace is the only place that is both writable and durable.** Most paths outside it refuse writes with permission denied, because you run as an unprivileged user — but world-writable locations like `{mountPoint}/tmp` accept them and are wiped when the container is recreated. Keep working files under `{home}/...` and set that as the working directory when a command writes relative files.
            - **Paths in command output are container-native.** `pwd`, `find`, `which` and the rest answer in the container's own spelling (`{native}/x`, without the mount point). Put `{mountPoint}` in front of one before handing it to a filesystem tool as a path — inside another command it works as it stands. The `cwd` the exec tool reports is the exception: it already comes back as a virtual path.

            {(servesMounts ? OtherMounts : "")}### Working here

            Edit files under `{home}/...` with the filesystem write tools, then run them with the exec tool. Both operate on the same volume.

            - **Persist results, not steps.** Keep working files in the persistent workspace. When the user wants the *result* somewhere durable, write a clean summary onto the mount that holds it — don't dump raw command output there.
            - **Be honest about what you ran.** Never claim you ran a command you didn't, and say so when one failed.
            """;
    }

    // The shell over the session's other mounts: where they are, what a command may do there, and
    // where it learns what actually happened.
    private const string OtherMounts = """
        ### The other mounts, inside a command

        - **They are directories.** The session's other mounts — never the machines, which are somebody else's computers — are at the paths the tools take: `/vault`, `/timers`, `/ha`. One the mounts section says is served only under `/vfs` is at `/vfs/<name>`. Work on their files in place — `grep -r`, `wc`, `jq`, `sed -i`, Python — with nothing copied into the workspace first; `exec` on such a mount runs here, in that directory.
        - **Actions run from scripts.** An action file runs as `./<name>` from its directory, or by its path, with its own output and exit code, so a script can branch on it. It cannot be read.
        - **Only what the file tools would do unasked.** A write is the text tool's create, `rm` the remove tool, `mv` the move tool, and the mount's own rules apply; what would need the person's approval is refused.
        - **Read `vfsChanges` before you report a change.** Bash says nothing when a mount refuses a write — `echo x > f` exits 0 — so the result lists every change the command made through the mounts: `applied`, `refused` with the mount's own reason, or `dropped` because a timeout cut it off. Say plainly what was refused or dropped. `vfsTruncated` names a directory a recursive command saw only part of.

        """;

    public static SkillText For(string mountPoint, string workspace, bool servesMounts = false) =>
        new(Name, Description, Body(mountPoint, workspace, servesMounts));

    // The trigger claim: a request of this kind loads the skill. Cited by the scenario that runs a
    // command, so a skill nobody loads shows as a red description rather than a red body. The
    // prose declares no other claim yet, as the section declared none.
    public static readonly PromptClaim LoadsForARun =
        new("sandbox.loads-for-a-run",
            "A request to run a command or script loads the sandbox skill before anything is executed.");

    public static readonly PromptClaim WorksOnTheMountsInPlace =
        new("sandbox.works-on-the-mounts-in-place",
            "A command over another mount's files reads them at that mount's own path inside the sandbox, rather than copying them into the workspace first.");

    public static readonly PromptClaim ReportsWhatVfsChangesSays =
        new("sandbox.reports-what-vfs-changes-says",
            "A change a command made through a mount is reported as the result's vfsChanges records it: a refused or dropped change is said to have failed, never claimed as done.");

    public static readonly PromptClaim ActionsRunFromScripts =
        new("sandbox.actions-run-from-scripts",
            "An action a script needs is run inside the command as ./<name> or by its path, not by a separate exec call beside it.");

    public static readonly IReadOnlyList<PromptClaim> Claims =
        [LoadsForARun, WorksOnTheMountsInPlace, ReportsWhatVfsChangesSays, ActionsRunFromScripts];
}