using System.IO.Enumeration;

namespace Domain.Tools.FileSystem;

// The one recursive enumeration both disk walks run on — the glob's and the text search's. Entries
// carry whether they are a directory, so a single pass answers the dirs-only rule that used to need
// a second walk over the same tree, and each entry can be counted exactly once.
//
// Reparse points are skipped by explicit option, and that guard is load-bearing rather than
// defensive: a link pointing at its own ancestor is exactly what the sandbox mount's alias looks
// like, and without the skip the walk keeps returning plausible entries and simply never ends.
// AttributesToSkip is set explicitly because the default also skips hidden and system entries,
// which these walks have always included.
public static class DiskWalk
{
    // What a walk over a disk root may cost, whatever the caller asked for. A mount whose root is a
    // container root has no natural end, so the bound is the walk's own rather than the pattern's:
    // fifty thousand entries enumerated, which a pattern excluding every candidate still cannot
    // exceed. These are constants, not settings — nothing deployment-specific decides them, so they
    // sit beside the response cap rather than in configuration, and a test lowers them by
    // overriding rather than by building a tree this size.
    public const int MaxEntriesScanned = 50_000;

    // The second cost a search has, and a different order of expense from enumerating a name:
    // opening and reading a file. A file pattern that excludes everything is bounded by the budget
    // above; one that excludes nothing is bounded by this. The glob never reads, so this is the
    // search's alone.
    public const int MaxFilesRead = 5_000;

    private static readonly EnumerationOptions _skipSymlinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public readonly record struct Entry(string Path, bool IsDirectory);

    public static IEnumerable<Entry> Entries(string root) =>
        new FileSystemEnumerable<Entry>(
            root,
            (ref FileSystemEntry entry) => new Entry(entry.ToFullPath(), entry.IsDirectory),
            _skipSymlinks);
}