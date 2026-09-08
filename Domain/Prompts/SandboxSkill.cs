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
        "Running anything in the Linux sandbox — a command, a script, a checksum, a pip install, a git clone — or keeping working files in its persistent workspace (\"compute the sha256 of that file\", \"run this python\", \"clone the repo and count the lines\"). Not for reading or writing files on another mount. The layout, what persists, what is preinstalled, how exit codes, output caps and timeouts come back, and how a container path maps to a virtual one.";

    public static string Body(string mountPoint, string workspace)
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
            - **Command execution.** Commands run via `bash -lc` inside the container. Each call is a fresh shell — environment variables and `cd` do **not** persist between calls; files written to the persistent workspace do. See the exec tool's description for argument details, the working directory, and limits.
            - **Preinstalled tooling.** `bash`, `python3` + `pip` + `venv`, `git`, `curl`, `jq`, `unzip`, plus the standard coreutils. Install extra Python packages with `pip install --user <package>` (user-scope; persists in your home).
            - **Network.** Full **outbound** network is available (you can `curl`, `git clone`, `pip install`). The sandbox does **not** publish inbound ports — external clients cannot reach a server you start inside it.

            ### Behaviour you should rely on

            - **Exit codes** are returned as data, not raised as errors — branch on them.
            - **Output is capped** per stream and the result flags truncation; for long output, redirect to a file and read it back with the file tools.
            - **Timeouts** kill the entire process tree and surface a timeout flag; raise the limit only when you genuinely need a longer-running command.
            - **The persistent workspace is the only place that is both writable and durable.** Most paths outside it refuse writes with permission denied, because you run as an unprivileged user — but world-writable locations like `{mountPoint}/tmp` accept them and are wiped when the container is recreated. Keep working files under `{home}/...` and set that as the working directory when a command writes relative files.
            - **Paths in command output are container-native.** `pwd`, `find`, `which` and the rest answer in the container's own spelling (`{native}/x`, without the mount point). Put `{mountPoint}` in front of one before handing it to a filesystem tool as a path — inside another command it works as it stands. The `cwd` the exec tool reports is the exception: it already comes back as a virtual path.

            ### Working here

            Edit files under `{home}/...` with the filesystem write tools, then run them with the exec tool. Both operate on the same volume.

            - **Persist results, not steps.** Keep working files in the persistent workspace. When the user wants the *result* somewhere durable, write a clean summary onto the mount that holds it — don't dump raw command output there.
            - **Be honest about what you ran.** Never claim you ran a command you didn't, and say so when one failed.
            """;
    }

    public static SkillText For(string mountPoint, string workspace) => new(Name, Description, Body(mountPoint, workspace));

    // The trigger claim: a request of this kind loads the skill. Cited by the scenario that runs a
    // command, so a skill nobody loads shows as a red description rather than a red body. The
    // prose declares no other claim yet, as the section declared none.
    public static readonly PromptClaim LoadsForARun =
        new("sandbox.loads-for-a-run",
            "A request to run a command or script loads the sandbox skill before anything is executed.");

    public static readonly IReadOnlyList<PromptClaim> Claims = [LoadsForARun];
}