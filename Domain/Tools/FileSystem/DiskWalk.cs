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