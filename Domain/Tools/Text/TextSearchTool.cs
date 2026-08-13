using System.Text.RegularExpressions;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Text;

public class TextSearchTool(string vaultPath, string[] allowedExtensions)
    : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Description = """
                                         Searches for text across files in the vault, or within a single file.

                                         Returns matching files with line numbers and context.
                                         To modify matching content, use the edit tool with a text target.

                                         Parameters:
                                         - query: Text or regex pattern to search for
                                         - regex: Treat query as regex pattern (default: false)
                                         - filePath: Optional. Search within this single file only (ignores directoryPath and filePattern)
                                         - filePattern: Glob pattern to filter files (e.g., "*.md")
                                         - directoryPath: Directory to search in (default: "/" for entire vault)
                                         - maxResults: Maximum number of matches to return (default: 50)
                                         - contextLines: Lines of context around each match (default: 1)

                                         Examples:
                                         - Find all mentions of "kubernetes": query="kubernetes"
                                         - Find in single file: query="config", filePath="docs/setup.md"
                                         - Find TODOs: query="TODO:.*", regex=true
                                         - Search only in docs: query="api", directoryPath="/docs"
                                         - Search markdown files: query="config", filePattern="*.md"
                                         """;

    private sealed record Scan(string Query, Regex Pattern, int ContextLines, int MaxResults, VfsTextSearchOutputMode OutputMode);

    // What a walk over this root covers, and why it stopped there. A search of one named file walks
    // nothing and reports neither.
    private sealed record Coverage(int EntriesScanned = 0, bool BudgetReached = false);

    // The same bounded matcher every other filesystem uses: a pattern that cannot compile comes
    // back as an envelope, and one that backtracks catastrophically ends as a timeout.
    private static readonly TimeSpan _matchTimeout = TimeSpan.FromSeconds(1);

    // Overridable for the same reason the match timeout is: a test reaches the bound by lowering it
    // rather than by building a tree the size of the shipped one.
    protected virtual int ScanBudget => DiskWalk.MaxEntriesScanned;

    protected virtual int ReadBudget => DiskWalk.MaxFilesRead;

    public FsResult<FsSearchResult> Run(
        string query,
        bool regex = false,
        string? filePath = null,
        string? filePattern = null,
        string directoryPath = "/",
        int maxResults = 50,
        int contextLines = 1,
        VfsTextSearchOutputMode outputMode = VfsTextSearchOutputMode.Content,
        CancellationToken cancellationToken = default)
    {
        if (!SearchRegex.Compile(query, regex, _matchTimeout).TryGetValue(out var pattern, out var patternError))
        {
            return new FsResult<FsSearchResult>.Err(patternError);
        }

        var scan = new Scan(query, pattern, contextLines, maxResults, outputMode);

        try
        {
            return filePath is not null
                ? SearchOneFile(filePath, regex, scan)
                : SearchDirectory(directoryPath, filePattern, regex, scan, cancellationToken);
        }
        catch (RegexMatchTimeoutException)
        {
            return new FsResult<FsSearchResult>.Err(SearchRegex.TimedOut(query));
        }
    }

    private FsResult<FsSearchResult> SearchOneFile(string filePath, bool regex, Scan scan)
    {
        if (!ResolveExistingFile(filePath).TryGetValue(out var fullPath, out var resolveError))
        {
            return new FsResult<FsSearchResult>.Err(resolveError);
        }

        var matches = MatchesIn(fullPath, scan, scan.MaxResults);

        return Build(filePath, regex, scan, filesSearched: 1, matches.Count == 0
            ? []
            : [BuildFileResult(ToRelativePath(fullPath), matches, scan.OutputMode)], matches.Count, new Coverage());
    }

    // The two costs a search over a tree has, each bounded by the budget that measures it:
    // enumerating a name, which a file pattern excluding everything still pays, and opening a file,
    // which a pattern excluding nothing pays for every candidate. Neither can run away, and the
    // caller's token ends the walk between entries.
    private FsResult<FsSearchResult> SearchDirectory(
        string directoryPath, string? filePattern, bool regex, Scan scan, CancellationToken cancellationToken)
    {
        if (!ResolveDirectory(directoryPath).TryGetValue(out var fullPath, out var resolveError))
        {
            return new FsResult<FsSearchResult>.Err(resolveError);
        }

        if (!VfsContentSearch.CompileFilePattern(filePattern, _matchTimeout)
                .TryGetValue(out var matchesPattern, out var patternError))
        {
            return new FsResult<FsSearchResult>.Err(patternError);
        }

        var results = new List<FsSearchFileResult>();
        var entriesScanned = 0;
        var filesSearched = 0;
        var totalMatches = 0;
        var budgetReached = false;

        foreach (var entry in DiskWalk.Entries(fullPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entriesScanned++;

            if (SearchEntry(entry, fullPath, filePattern, matchesPattern, scan,
                    scan.MaxResults - totalMatches) is { } matches)
            {
                filesSearched++;
                if (matches.Count > 0)
                {
                    results.Add(BuildFileResult(ToRelativePath(entry.Path), matches, scan.OutputMode));
                    totalMatches += matches.Count;
                }
            }

            if (totalMatches >= scan.MaxResults)
            {
                break;
            }

            if (entriesScanned >= ScanBudget || filesSearched >= ReadBudget)
            {
                budgetReached = true;
                break;
            }
        }

        return Build(directoryPath, regex, scan, filesSearched, results, totalMatches,
            new Coverage(entriesScanned, budgetReached));
    }

    // Null for an entry this search never opens — a directory, a disallowed extension, a name the
    // file pattern excludes — which is what keeps it out of the files-read count and its budget.
    private IReadOnlyList<FsSearchMatch>? SearchEntry(
        DiskWalk.Entry entry, string root, string? filePattern,
        Func<string, bool> matchesPattern, Scan scan, int remaining) =>
        entry.IsDirectory
        || !IsAllowedExtension(entry.Path)
        || !matchesPattern(PatternCandidate(root, entry.Path, filePattern))
            ? null
            : MatchesIn(entry.Path, scan, remaining);

    private static FsResult<FsSearchResult> Build(
        string path, bool regex, Scan scan, int filesSearched,
        IReadOnlyList<FsSearchFileResult> results, int totalMatches, Coverage coverage) =>
        new FsResult<FsSearchResult>.Ok(new FsSearchResult
        {
            Query = scan.Query,
            Regex = regex,
            Path = path,
            FilesSearched = filesSearched,
            FilesWithMatches = results.Count,
            TotalMatches = totalMatches,
            Truncated = totalMatches >= scan.MaxResults,
            EntriesScanned = coverage.EntriesScanned,
            BudgetReached = coverage.BudgetReached,
            Results = results
        });

    private static FsSearchFileResult BuildFileResult(
        string file, IReadOnlyList<FsSearchMatch> matches, VfsTextSearchOutputMode outputMode) =>
        outputMode == VfsTextSearchOutputMode.FilesOnly
            ? new FsSearchFileResult { File = file, MatchCount = matches.Count }
            : new FsSearchFileResult { File = file, Matches = matches };

    // The jail vets the search root, but a symlink discovered inside the tree can point anywhere —
    // following it would serve foreign file content as search results, and a link pointing at its
    // own ancestor would never end — so the walk skips symlinks wholesale. That guard is DiskWalk's,
    // shared with the glob, which is also why the caller's filePattern never reaches an enumeration
    // API: .NET resolves a leading "../" inside a search pattern, so "../*.md" would read above the
    // vault. Everything under the vetted root is enumerated and names are filtered here, with the
    // same compiled matcher every other filesystem uses.

    // A bare pattern ("*.md") filters file names at any depth, as it did when EnumerateFiles
    // matched it per directory; a pattern with a separator ("docs/*.md") filters the path
    // relative to the searched directory.
    private static string PatternCandidate(string root, string file, string? filePattern) =>
        filePattern?.Contains('/') == true
            ? Path.GetRelativePath(root, file).Replace('\\', '/')
            : Path.GetFileName(file);

    private bool IsAllowedExtension(string filePath) =>
        AllowedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());

    // An unreadable file is not a failure of the search — skip it and keep scanning the rest. Only
    // the read is guarded: a match timeout must reach the caller as its own envelope.
    private static IReadOnlyList<FsSearchMatch> MatchesIn(string filePath, Scan scan, int maxMatches)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return lines
            .Select((text, index) => (Text: text, Index: index))
            .Where(line => IsMatchingLine(line.Text, scan))
            .Take(maxMatches)
            .Select(line => BuildMatch(lines, line.Index, scan.ContextLines))
            .ToList();
    }

    private static bool IsMatchingLine(string line, Scan scan) => scan.Pattern.IsMatch(line);

    private static FsSearchMatch BuildMatch(string[] lines, int index, int contextLines)
    {
        var before = contextLines > 0 ? Context(lines.Take(index).TakeLast(contextLines)) : [];
        var after = contextLines > 0 ? Context(lines.Skip(index + 1).Take(contextLines)) : [];

        return new FsSearchMatch
        {
            Line = index + 1,
            Text = Truncate(lines[index], 200),
            Section = FindNearestHeading(lines, index),
            Context = before.Count > 0 || after.Count > 0
                ? new FsSearchContext { Before = before, After = after }
                : null
        };
    }

    private static IReadOnlyList<string> Context(IEnumerable<string> lines) =>
        lines.Select(l => Truncate(l, 100)).ToList();

    private static string? FindNearestHeading(string[] lines, int lineIndex) =>
        lines
            .Take(lineIndex + 1)
            .Reverse()
            .FirstOrDefault(l => l.StartsWith('#'))
            ?.TrimStart('#')
            .Trim();

    private static string Truncate(string text, int maxLength) =>
        text.Length > maxLength ? text[..maxLength] + "..." : text;

    private string ToRelativePath(string fullPath) =>
        Path.GetRelativePath(VaultPath, fullPath).Replace('\\', '/');

    // A search path is always vault-relative: a leading '/' means the vault root, not the OS root.
    // Containment is then decided by the one jail, like every other disk tool.
    private FsResult<string> ResolveDirectory(string path)
    {
        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = string.IsNullOrEmpty(normalized)
            ? Jail.Root
            : Path.GetFullPath(Path.Combine(Jail.Root, normalized));

        if (!Jail.Contains(fullPath))
        {
            return FsError.Invalid<string>(Jail.DeniedMessage);
        }

        return Directory.Exists(fullPath)
            ? new FsResult<string>.Ok(fullPath)
            : FsError.NotFound<string>(path);
    }
}