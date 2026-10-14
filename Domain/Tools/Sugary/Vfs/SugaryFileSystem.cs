using System.Text;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Sugary;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Sugary.Vfs;

// Her carb log as files, rendered from the store on every read and kept nowhere: the database is
// the only copy, so a meal saved a moment ago is in the next read and nothing on disk can go stale.
// Read, glob, info and search are the whole surface — nothing that writes is overridden, so no
// writing tool exists on this mount and a write routed here is refused with what it can do.
public sealed class SugaryFileSystem(
    ISugaryLog log, TimeZoneInfo homeZone, TimeSpan? regexMatchTimeout = null) : FileSystemBackendBase
{
    public const string Name = "sugary";

    public override string FilesystemName => Name;

    protected override TimeSpan SearchMatchTimeout => regexMatchTimeout ?? base.SearchMatchTimeout;

    public override string DescribeMount =>
        "Her carb-counting log from Sugary (she has type 1 diabetes), read-only and always current: "
        + "meals/<YYYY-MM>.csv (one row per meal), items/<YYYY-MM>.csv (one row per food in a meal), "
        + "foods.csv (the foods she keeps) and README.md, which explains every column — read it before "
        + $"reading a number. Times carry their offset in the home's zone ({homeZone.Id}).";

    public override string DescribeRead =>
        "Read a file of her carb log: README.md, foods.csv, meals/<YYYY-MM>.csv or items/<YYYY-MM>.csv. "
        + "The CSV comes back exactly as it is; offset and limit (1-based lines) read a window of it.";

    public override string DescribeInfo => "Get info about a path of her carb log: exists, isDirectory, size.";

    public override string DescribeGlob =>
        "List the files of her carb log matching a glob under basePath — one meals and one items file "
        + "per month she logged a meal in. `*` matches one path segment, `**` recurses, `?` one char, "
        + "`{a,b}` brace alternation. A trailing slash (e.g. `*/`) lists directories only.";

    public override string DescribeSearch =>
        "Search the text of her carb log, e.g. a food's name across every month. Scope with path (one "
        + "file) or directoryPath (meals or items); omit both to search everything. Supports regex, "
        + "filePattern, maxResults, contextLines, and outputMode (content|filesOnly).";

    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public override async Task<FsResult<FsReadResult>> ReadAsync(
        string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = SugaryPath.Parse(path);
        if (node.IsDirectory)
        {
            return Invalid<FsReadResult>($"'{path}' is a directory; glob it to see its files.");
        }

        return await RenderAsync(node, ct) is { } text
            ? new FsResult<FsReadResult>.Ok(Window(path, text, offset, limit))
            : NotFound<FsReadResult>(path);
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        var node = SugaryPath.Parse(path);
        var text = node.IsDirectory ? null : await RenderAsync(node, ct);
        var exists = node.IsDirectory || text is not null;
        return new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = exists,
            Path = path,
            IsDirectory = exists ? node.IsDirectory : null,
            Size = text is null ? null : _utf8.GetByteCount(text)
        });
    }

    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        if (!GlobPrologue(basePath, pattern).TryGetValue(out var scope, out var invalidPattern))
        {
            return new FsResult<FsGlobResult>.Err(invalidPattern);
        }

        var (dirsOnly, matches) = scope;
        var nodes = await NodesAsync(ct);
        return Glob(pattern, () => nodes
            .Where(node => node.Kind != SugaryNodeKind.Root && (!dirsOnly || node.IsDirectory))
            .Where(node => matches(node.Path.TrimStart('/')))
            .Select(node => node.IsDirectory ? node.Path + "/" : node.Path)
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList());
    }

    public override async Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
        string? directoryPath, string? filePattern, int maxResults, int contextLines,
        VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        if (!CompileFilePattern(filePattern).TryGetValue(out var admits, out var patternError))
        {
            return new FsResult<FsSearchResult>.Err(patternError);
        }

        var scope = SugaryPath.Parse(path ?? directoryPath);
        var files = (await NodesAsync(ct))
            .Where(node => !node.IsDirectory && Within(node, scope) && admits(node.FileName))
            .OrderBy(node => node.Path, StringComparer.Ordinal)
            .ToList();

        return await SearchNodesAsync(
            files,
            async (node, token) => (node.Path, await RenderAsync(node, token)),
            new FsSearchScan
            {
                Query = query,
                Regex = regex,
                Path = path ?? directoryPath ?? "/",
                MaxResults = maxResults,
                ContextLines = contextLines,
                OutputMode = outputMode
            },
            ct);
    }

    private static bool Within(SugaryNode file, SugaryNode scope) => scope.Kind switch
    {
        SugaryNodeKind.Root => true,
        SugaryNodeKind.MealsDir => file.Kind == SugaryNodeKind.MealsMonth,
        SugaryNodeKind.ItemsDir => file.Kind == SugaryNodeKind.ItemsMonth,
        SugaryNodeKind.Unknown => false,
        _ => file == scope
    };

    // Every node that exists right now: the fixed four and a pair of files for each month she ate in.
    private async Task<IReadOnlyList<SugaryNode>> NodesAsync(CancellationToken ct)
    {
        var months = await MonthsAsync(ct);
        SugaryNode[] fixedNodes =
        [
            new(SugaryNodeKind.Root), new(SugaryNodeKind.Readme), new(SugaryNodeKind.Foods),
            new(SugaryNodeKind.MealsDir), new(SugaryNodeKind.ItemsDir)
        ];
        return fixedNodes
            .Concat(months.SelectMany(month => new SugaryNode[]
            {
                new(SugaryNodeKind.MealsMonth, month), new(SugaryNodeKind.ItemsMonth, month)
            }))
            .ToList();
    }

    private async Task<IReadOnlyList<string>> MonthsAsync(CancellationToken ct) =>
        (await log.MealTimesAsync(ct))
        .Select(time => SugaryTables.Month(time, homeZone))
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToList();

    // The file's text, or null where there is no such file.
    private async Task<string?> RenderAsync(SugaryNode node, CancellationToken ct) => node.Kind switch
    {
        SugaryNodeKind.Readme => SugaryReadme.For(homeZone),
        SugaryNodeKind.Foods => SugaryTables.Foods(await log.FoodsAsync(ct)),
        SugaryNodeKind.MealsMonth => await MealsInAsync(node.Month!, ct) is { Count: > 0 } meals
            ? SugaryTables.Meals(meals, homeZone)
            : null,
        SugaryNodeKind.ItemsMonth => await MealsInAsync(node.Month!, ct) is { Count: > 0 } meals
            ? SugaryTables.Items(meals, homeZone)
            : null,
        _ => null
    };

    // A month on the home's calendar starts and ends at an offset that can differ from UTC's by up
    // to a day either way, so the store is asked for a day more each side and the home's calendar
    // decides which meals are in.
    private async Task<IReadOnlyList<LoggedMeal>> MealsInAsync(string month, CancellationToken ct)
    {
        var first = DateTimeOffset.ParseExact(
            month + "-01T00:00:00Z", "yyyy-MM-dd'T'HH:mm:ssK", System.Globalization.CultureInfo.InvariantCulture);
        var meals = await log.MealsAsync(first.AddDays(-1), first.AddMonths(1).AddDays(1), ct);
        return meals.Where(meal => SugaryTables.Month(meal.EatenAt, homeZone) == month).ToList();
    }

    private static FsReadResult Window(string path, string text, int? offset, int? limit)
    {
        var lines = text.Split('\n');
        if (offset is null && limit is null)
        {
            return new FsReadResult { FilePath = path, Content = text, TotalLines = lines.Length, Truncated = false };
        }

        var start = Math.Clamp((offset ?? 1) - 1, 0, lines.Length);
        var take = Math.Min(limit ?? lines.Length, lines.Length - start);
        var truncated = start + take < lines.Length;
        return new FsReadResult
        {
            FilePath = path,
            Content = string.Join('\n', lines.Skip(start).Take(take)),
            TotalLines = lines.Length,
            Truncated = truncated,
            Suggestion = truncated ? $"Use offset={start + take + 1} to continue reading." : null
        };
    }
}