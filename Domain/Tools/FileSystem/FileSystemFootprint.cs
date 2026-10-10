using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.FileSystem;

// What a filesystem call touches: the virtual paths in its arguments, resolved the way the tools
// resolve them, so two spellings of one path are one resource — the forgotten slash, a machine
// address in another case, a trailing slash. A directory names everything under it, which is
// what makes removing /timers/pasta and creating /timers/pasta/timer.json a conflict, and a glob
// or a search a reader of its whole subtree.
public sealed class FileSystemFootprint(IVirtualFileSystemRegistry registry)
{
    public static readonly SharedResource Whole = SharedResource.Of("filesystem");

    public CallFootprint Reading(IReadOnlyDictionary<string, object?> arguments, params string[] pathArguments) =>
        CallFootprint.Reading([.. pathArguments.Select(name => At(CallArguments.Text(arguments, name)))]);

    public CallFootprint Changing(IReadOnlyDictionary<string, object?> arguments, params string[] pathArguments) =>
        CallFootprint.Changing([.. pathArguments.Select(name => At(CallArguments.Text(arguments, name)))]);

    // A command can touch anything its shell reaches, and nothing in its text says what. In the
    // sandbox that is every mount a command is served through the bridge, so it is taken as the
    // whole filesystem; on somebody's own machine it is that machine. A call with no shell behind
    // it is one action out of a mount's closed catalog, run where it stands: two lights switched
    // in one message have nothing to wait for in each other.
    public CallFootprint Executing(ExecReach reach, IReadOnlyDictionary<string, object?> arguments)
    {
        var path = CallArguments.Text(arguments, "path");
        return path is null
            ? CallFootprint.Changing(Whole)
            : reach.Of(path, CallArguments.Text(arguments, "command") ?? "") switch
            {
                ShellReach.Contained => CallFootprint.Changing(Whole),
                ShellReach.Host => CallFootprint.Changing(MountOf(path)),
                _ => CallFootprint.Changing(At(path))
            };
    }

    // A path no mount serves, and a path nobody gave, claim the whole filesystem: the call will
    // be refused and touch nothing, but a refusal is not this class's to predict.
    public SharedResource At(string? path) =>
        Resolve(path) is { } resolution
            ? Segments(resolution.RelativePath) is { } segments
                ? Whole.Under(resolution.MountPoint).Under(segments)
                : Whole.Under(resolution.MountPoint)
            : Whole;

    private SharedResource MountOf(string path) =>
        Resolve(path) is { } resolution ? Whole.Under(resolution.MountPoint) : Whole;

    private FileSystemResolution? Resolve(string? path) =>
        !string.IsNullOrWhiteSpace(path) && registry.Resolve(path).TryGetValue(out var resolution, out _)
            ? resolution
            : null;

    // Null for a path that climbs: where `..` lands is the backend's to decide, so the claim
    // widens to the mount rather than guessing.
    private static string[]? Segments(string relativePath)
    {
        var segments = relativePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment != ".")
            .ToArray();
        return segments.Contains("..") ? null : segments;
    }
}