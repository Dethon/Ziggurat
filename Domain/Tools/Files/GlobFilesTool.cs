using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Files;

public class GlobFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    public const int FileResultCap = 200;

    private readonly PathJail _jail = new(libraryPath.BaseLibraryPath);

    public async Task<FsResult<FsGlobResult>> Run(string pattern, CancellationToken cancellationToken, string? basePath = null)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return FsError.Invalid<FsGlobResult>("Pattern must not be empty.");
        }

        // A whole '..' segment, not the substring: v1..2.md is just a name. The check stays here
        // because a relative pattern is handed raw to the matcher, never resolved through the jail.
        if (HasDotDotSegment(pattern) || (basePath is not null && HasDotDotSegment(basePath)))
        {
            return FsError.Invalid<FsGlobResult>("Pattern and basePath must not contain '..' segments.");
        }

        var matcherRoot = MatcherRoot(_jail.Root, basePath);

        if (!_jail.Contains(matcherRoot))
        {
            return FsError.Invalid<FsGlobResult>("basePath must resolve under the mount root.");
        }

        if (ToMatcherRelative(matcherRoot, pattern) is not { } scoped)
        {
            return FsError.Invalid<FsGlobResult>(
                "Absolute pattern must be under the glob root (the mount root plus basePath).");
        }

        pattern = scoped;

        // The matcher's timeout is raised while the walk is being pulled, not when it is asked for,
        // so the envelope every other mount answers wraps the loop below.
        return await GlobRegex.GuardedAsync(pattern, () => Collect(matcherRoot, basePath, pattern, cancellationToken));
    }

    private async Task<FsResult<FsGlobResult>> Collect(
        string matcherRoot, string? basePath, string pattern, CancellationToken cancellationToken)
    {
        var found = new List<string>();
        GlobWalk walk;
        try
        {
            walk = client.Glob(matcherRoot, pattern, cancellationToken);
            await foreach (var hit in walk.Matches.WithCancellation(cancellationToken))
            {
                found.Add(hit);
                // One more match than the response carries is all it takes to report truncation,
                // and past it there is nothing left to find that anyone will see. The cap is a
                // response-shaping concern, so it ends the walk from here rather than in the client.
                if (found.Count > FileResultCap)
                {
                    break;
                }
            }
        }
        // A lazy walk raises a missing base directory on the first pull rather than at the call,
        // so the catch that turns it into the not-found envelope wraps the pull.
        catch (DirectoryNotFoundException)
        {
            return FsError.NotFound<FsGlobResult>(basePath ?? matcherRoot);
        }
        catch (ArgumentException ex)
        {
            // The client refuses a pattern it cannot expand (the brace-expansion cap); surface it
            // as the invalid-pattern envelope, like every other bad pattern.
            return FsError.Invalid<FsGlobResult>(ex.Message);
        }

        // Return entries relative to the mount root (the disk client yields absolute paths), sorted
        // for presentation. The agent-side VFS tool re-prefixes the mount point, so every filesystem
        // speaks one format.
        var relative = found
            .Select(p => ToMountRelative(_jail.Root, p))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var capped = relative.Length > FileResultCap;

        return new FsResult<FsGlobResult>.Ok(new FsGlobResult
        {
            Entries = capped ? relative.Take(FileResultCap).ToArray() : relative,
            Truncated = capped,
            Total = relative.Length,
            EntriesScanned = walk.EntriesScanned,
            BudgetReached = walk.BudgetReached
        });
    }

    // The root the matcher actually runs from: the mount root, scoped by basePath.
    public static string MatcherRoot(string mountRoot, string? basePath) =>
        string.IsNullOrEmpty(basePath)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountRoot))
            : Path.GetFullPath(Path.Combine(Path.GetFullPath(mountRoot), basePath.TrimStart('/')));

    // An absolute pattern relativized against that root — relativizing against the mount root while
    // matching under root+basePath double-scoped. A relative pattern is already what the matcher
    // wants; null means the pattern points outside the root, which is the caller's error. Public
    // because a mount that matches a second, virtual set of candidates beside the disk one (the
    // media library's downloads overlay) has to scope its pattern the same way, and two spellings
    // of this rule would be two answers to one glob.
    public static string? ToMatcherRelative(string matcherRoot, string pattern)
    {
        if (!Path.IsPathRooted(pattern))
        {
            return pattern;
        }

        var dirsOnly = pattern.EndsWith('/');
        var trimmed = pattern.TrimEnd('/');
        return trimmed.Equals(matcherRoot, StringComparison.Ordinal)
               || trimmed.StartsWith(matcherRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? Path.GetRelativePath(matcherRoot, trimmed) + (dirsOnly ? "/" : "")
            : null;
    }

    private static bool HasDotDotSegment(string path) =>
        path.Split('/', '\\').Any(segment => segment == "..");

    private static string ToMountRelative(string baseRoot, string absolute)
    {
        var isDirectory = absolute.EndsWith('/');
        var relative = Path.GetRelativePath(baseRoot, absolute.TrimEnd('/')).Replace('\\', '/');
        return isDirectory ? relative + "/" : relative;
    }
}