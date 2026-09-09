using Domain.Contracts;

namespace Domain.Prompts;

// The sandbox's standing prose: that it is the one mount with exec, and that a run loads the
// sandbox skill, which carries everything else. Built from the mount point the mount declares
// rather than asserting it, as the skill body is, so a rename cannot leave it quietly wrong.
public static class SandboxPrompt
{
    public const string Name = "sandbox_prompt";

    // `workspace` arrives in the backend's own coordinates, as the mount publishes it, and is
    // composed into a virtual path here by the one translation — the model never reads a backend
    // spelling (ADR 0016).
    public static string Build(string mountPoint, string workspace)
    {
        var home = FileSystemResolution.ToVirtualPath(mountPoint, workspace);

        return $"""
            ## Sandbox Filesystem

            You have access to a Linux sandbox container exposed as the virtual filesystem mounted at `{mountPoint}`. Among the filesystems available to you, the sandbox is the **only** one that supports command execution — other mounts return an "unsupported operation" error envelope if you try.

            Your **persistent workspace** is `{home}`; it is the only place that is both writable and durable. Before you run anything here — a command, a script, an install — load the `sandbox` skill: the layout, what is preinstalled, how exit codes, output and timeouts come back and how a container path maps to a virtual one are there.

            A path the user names that is under no mount is not somewhere on this box either. One look at it, spelled as given, is the whole search: if it is not there, the answer is that it is not reachable — no `find`, no glob from the root, no look through the workspace or the system directories for a folder of that name.
            """;
    }
}