using Domain.Contracts;

namespace Domain.Prompts;

// The sandbox's own prose, built from the two values the mount declares about itself rather than
// asserting either. It used to spell the mount point eight times and the workspace four, and the
// guard test pins the mount point to the image alias rather than to this text — so a rename would
// have left every sentence here quietly wrong with the whole suite still green.
public static class SandboxPrompt
{
    // `workspace` arrives in the backend's own coordinates, as the mount publishes it, and is
    // composed into a virtual path here by the one translation — the model never reads a backend
    // spelling (ADR 0016).
    public static string Build(string mountPoint, string workspace)
    {
        var home = FileSystemResolution.ToVirtualPath(mountPoint, workspace);

        return $"""
            ## Sandbox Filesystem

            You have access to a Linux sandbox container exposed as the virtual filesystem mounted at `{mountPoint}`. Among the filesystems available to you, the sandbox is the **only** one that supports command execution — other mounts return an "unsupported operation" error envelope if you try.

            ### Layout

            - `{mountPoint}` — the container root (`/`), for every tool including command execution. The container knows this name too, so `{mountPoint}/etc/os-release` and `/etc/os-release` are the same file whether you write one as a path argument or inside a command.
            - `{home}` — the **persistent workspace** (a Docker named volume). Files here survive container restarts, and files someone sends you are put here. Nothing puts you here by default: name it as the working directory when you want to work in it.
            - `{mountPoint}/etc`, `{mountPoint}/usr`, `{mountPoint}/tmp`, etc. — system directories. They reset whenever the container is recreated and you typically cannot write to them (you run as an unprivileged user) — the container root included, so a command that writes a relative file needs a working directory you own.

            ### Capabilities

            - **File operations.** Standard read/write/glob/search/move/remove all work here as on any other mount.
            - **Command execution.** Commands run via `bash -lc` inside the container. Each call is a fresh shell — environment variables and `cd` do **not** persist between calls; files written to the persistent workspace do. See the exec tool's description for argument details, the working directory, and limits.
            - **Preinstalled tooling.** `bash`, `python3` + `pip` + `venv`, `git`, `curl`, `jq`, `unzip`, plus the standard coreutils. Install extra Python packages with `pip install --user <package>` (user-scope; persists in your home).
            - **Network.** Full **outbound** network is available (you can `curl`, `git clone`, `pip install`). The sandbox does **not** publish inbound ports — external clients cannot reach a server you start inside it.

            ### Behaviour you should rely on

            - **Exit codes** are returned as data, not raised as errors — branch on them.
            - **Output is capped** per stream and the result flags truncation; for long output, redirect to a file and read it back with the file tools.
            - **Timeouts** kill the entire process tree and surface a timeout flag; raise the limit only when you genuinely need a longer-running command.
            - **Writes outside the persistent workspace** fail with permission denied, because you run as an unprivileged user; keep working files under `{home}/...` and set that as the working directory when a command writes relative files.
            - **Paths in command output are container-native.** `pwd`, `find`, `which` and the rest answer in the container's own spelling (`{workspace}/x`, without the mount point). Put `{mountPoint}` in front of one before handing it to a filesystem tool as a path — inside another command it works as it stands. The `cwd` the exec tool reports is the exception: it already comes back as a virtual path.

            ### Working here

            Edit files under `{home}/...` with the filesystem write tools, then run them with the exec tool. Both operate on the same volume.

            - **Persist results, not steps.** Keep working files in the persistent workspace. When the user wants the *result* somewhere durable, write a clean summary onto the mount that holds it — don't dump raw command output there.
            - **Be honest about what you ran.** Never claim you ran a command you didn't, and say so when one failed.
            """;
    }
}