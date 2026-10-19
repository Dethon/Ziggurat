using System.Globalization;
using System.Text;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Sugary;
using Domain.DTOs.Tables;
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
        + "doses/<YYYY-MM>.csv (one row per insulin dose she recorded taking), "
        + "exercise/<YYYY-MM>.csv (one row per session of exercise she recorded), "
        + "foods.csv (the foods she keeps), periods.csv (her menstrual periods), day-notes.csv (what she "
        + "wrote about a day) and README.md, which explains every column — read it before "
        + $"reading a number. Times carry their offset in the home's zone ({homeZone.Id}).";

    public override string DescribeRead =>
        "Read a file of her carb log: README.md, foods.csv, periods.csv, day-notes.csv, meals/<YYYY-MM>.csv, "
        + "items/<YYYY-MM>.csv, doses/<YYYY-MM>.csv or exercise/<YYYY-MM>.csv. "
        + "The CSV comes back exactly as it is; offset and limit (1-based lines) read a window of it.";

    public override string DescribeInfo => "Get info about a path of her carb log: exists, isDirectory, size.";

    public override string DescribeGlob =>
        "List the files of her carb log matching a glob under basePath — one meals and one items file "
        + "per month she logged a meal in, one doses file per month she recorded a dose in, one exercise "
        + "file per month she started a session in. "
        + "`*` matches one path segment, `**` recurses, `?` one char, "
        + "`{a,b}` brace alternation. A trailing slash (e.g. `*/`) lists directories only.";

    public override string DescribeSearch =>
        "Search the text of her carb log, e.g. a food's name across every month, or a meal_id among the "
        + "doses. Scope with path (one file) or directoryPath (meals, items, doses or exercise); omit both to "
        + "search everything. Supports regex, "
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
        SugaryNodeKind.DosesDir => file.Kind == SugaryNodeKind.DosesMonth,
        SugaryNodeKind.ExerciseDir => file.Kind == SugaryNodeKind.ExerciseMonth,
        SugaryNodeKind.Unknown => false,
        _ => file == scope
    };

    // Every node that exists right now: the fixed ones, a pair of files for each month she ate in and
    // a doses file for each month she took a dose in and an exercise file for each month she started
    // a session in — the sets of months independent, so a month of basal alone still has its file.
    private async Task<IReadOnlyList<SugaryNode>> NodesAsync(CancellationToken ct)
    {
        var mealMonths = Months(await log.MealTimesAsync(ct));
        var doseMonths = Months(await log.DoseTimesAsync(ct));
        var exerciseMonths = Months(await log.ExerciseTimesAsync(ct));
        SugaryNode[] fixedNodes =
        [
            new(SugaryNodeKind.Root), new(SugaryNodeKind.Readme), new(SugaryNodeKind.Foods),
            new(SugaryNodeKind.Periods), new(SugaryNodeKind.DayNotes),
            new(SugaryNodeKind.MealsDir), new(SugaryNodeKind.ItemsDir), new(SugaryNodeKind.DosesDir),
            new(SugaryNodeKind.ExerciseDir)
        ];
        return fixedNodes
            .Concat(mealMonths.SelectMany(month => new SugaryNode[]
            {
                new(SugaryNodeKind.MealsMonth, month), new(SugaryNodeKind.ItemsMonth, month)
            }))
            .Concat(doseMonths.Select(month => new SugaryNode(SugaryNodeKind.DosesMonth, month)))
            .Concat(exerciseMonths.Select(month => new SugaryNode(SugaryNodeKind.ExerciseMonth, month)))
            .ToList();
    }

    private IReadOnlyList<string> Months(IEnumerable<DateTimeOffset> times) =>
        times
            .Select(time => SugaryTables.Month(time, homeZone))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    // The file's text, or null where there is no such file.
    private async Task<string?> RenderAsync(SugaryNode node, CancellationToken ct) =>
        node.Kind == SugaryNodeKind.Readme
            ? SugaryReadme.For(homeZone)
            : await TableAsync(node, ct) is { } table ? SugaryCsv.Write(table) : null;

    // The table a file holds, or null where there is no such file: a month with nothing logged in it
    // has none.
    private async Task<Table?> TableAsync(SugaryNode node, CancellationToken ct) => node.Kind switch
    {
        SugaryNodeKind.Foods => SugaryTables.Foods(await log.FoodsAsync(ct)),
        SugaryNodeKind.Periods => SugaryTables.Periods(await log.PeriodsAsync(ct)),
        SugaryNodeKind.DayNotes => SugaryTables.DayNotes(await log.DayNotesAsync(ct)),
        SugaryNodeKind.MealsMonth => await MealsInAsync(node.Month!, ct) is { Count: > 0 } meals
            ? SugaryTables.Meals(meals, homeZone)
            : null,
        SugaryNodeKind.ItemsMonth => await MealsInAsync(node.Month!, ct) is { Count: > 0 } meals
            ? SugaryTables.Items(meals, homeZone)
            : null,
        SugaryNodeKind.DosesMonth => await InMonthAsync(node.Month!, log.DosesAsync, dose => dose.TakenAt, ct)
            is { Count: > 0 } doses
            ? SugaryTables.Doses(doses, homeZone)
            : null,
        SugaryNodeKind.ExerciseMonth => await InMonthAsync(
                node.Month!, log.ExerciseAsync, session => session.StartedAt, ct)
            is { Count: > 0 } sessions
            ? SugaryTables.Exercise(sessions, homeZone)
            : null,
        _ => null
    };

    // A month on the home's calendar starts and ends at an offset that can differ from UTC's by up
    // to a day either way, so the store is asked for a day more each side and the home's calendar
    // decides which meals are in. The calendar's first and last months have no day beyond them to
    // ask for, and no meal in them, so they are simply empty.
    private Task<IReadOnlyList<LoggedMeal>> MealsInAsync(string month, CancellationToken ct) =>
        InMonthAsync(month, log.MealsAsync, meal => meal.EatenAt, ct);

    private async Task<IReadOnlyList<T>> InMonthAsync<T>(
        string month,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<IReadOnlyList<T>>> between,
        Func<T, DateTimeOffset> when,
        CancellationToken ct)
    {
        var first = DateTimeOffset.ParseExact(
            month + "-01T00:00:00Z", "yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);
        if (first.Year == DateTimeOffset.MinValue.Year && first.Month == 1
            || first.Year == DateTimeOffset.MaxValue.Year && first.Month == 12)
        {
            return [];
        }

        var rows = await between(first.AddDays(-1), first.AddMonths(1).AddDays(1), ct);
        return rows.Where(row => SugaryTables.Month(when(row), homeZone) == month).ToList();
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