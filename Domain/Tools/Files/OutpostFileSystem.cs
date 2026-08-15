using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

// A machine's own filesystem, served as an ordinary mount. It is a text disk root and nothing more
// exotic: the same operations, the same virtual paths, the same refusal envelopes as the sandbox,
// which is the whole point — there is nothing new to learn per machine.
//
// The mount root is always the machine's root. A jail is not a different root; it is a refusal
// rule, so the same file has the same name whether the outpost is jailed or not.
public class OutpostFileSystem(
    string filesystemName,
    IFileSystemClient client,
    string workingDirectory,
    string[] allowedExtensions)
    : TextDiskFileSystem(
        filesystemName,
        // Placeholder: the real prose is generated below from the values this was started with,
        // and the base's stored copy is never read because DescribeMount is overridden.
        "",
        client,
        new LibraryPathConfig(MountRoot),
        allowedExtensions)
{
    public const string MountRoot = "/";

    private readonly string _workingDirectory = Rooted(workingDirectory);

    // The working directory is where files land and commands run, so it is what this mount
    // declares as its workspace — published, like every other mount's, in the backend's own
    // coordinates, which here are the machine's own absolute paths minus the leading slash.
    public override string Workspace => _workingDirectory.TrimStart('/');

    // Generated rather than written, so the prose cannot disagree with the behaviour: every fact
    // in it is read off the values the binary was started with, and there is no separate prompt
    // that could be edited into disagreeing.
    public override string DescribeMount =>
        $"{FilesystemName} — a filesystem on a real machine, mounted at {MountPoint} with the "
        + $"machine's own root as the mount root. Working directory: {_workingDirectory} (files "
        + "land there and it is this mount's workspace). "
        + (Jailed
            ? $"Jailed: every path outside {_workingDirectory} is refused, and glob and text search "
              + "start their walk there."
            : "Not jailed: any path on the machine can be reached.")
        + (Executes
            ? $" Commands can be run on this machine with fs_exec, in {_workingDirectory}."
            : " Commands cannot be run on this machine.")
        + " The machine decides whether this mount exists, so it can be absent for reasons that are "
        + "nobody's fault.";

    // Asks the same question the tool registrar asks — has this operation been overridden — rather
    // than carrying a flag beside the override that could drift from it.
    protected bool Executes =>
        GetType().GetMethod(nameof(ExecAsync))?.DeclaringType != typeof(FileSystemBackendBase);

    protected virtual bool Jailed => false;

    protected string WorkingDirectory => _workingDirectory;

    // A working directory is a path on somebody's machine, so it is absolute — a relative one has
    // nothing to be relative to when the binary is started from anywhere. The machine root itself
    // is refused because it is a jail that jails nothing and a workspace that is the mount root,
    // which is the silent nothing ADR 0025 exists to remove; an outpost meant to reach the whole
    // machine is simply left unjailed.
    private static string Rooted(string workingDirectory)
    {
        var trimmed = workingDirectory?.TrimEnd('/');
        return string.IsNullOrEmpty(trimmed) || !Path.IsPathRooted(workingDirectory)
            ? throw new ArgumentException(
                $"The working directory '{workingDirectory}' must be an absolute path on this "
                + "machine, and cannot be the machine root: leave the outpost unjailed to reach "
                + "the whole machine.",
                nameof(workingDirectory))
            : trimmed;
    }
}