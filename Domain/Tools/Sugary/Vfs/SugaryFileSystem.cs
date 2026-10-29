using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Sugary;
using Domain.DTOs.Tables;
using Domain.Tools;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Sugary.Vfs;

// Her carb log as files, rendered from the store on every read and kept nowhere: the database is
// the only copy, so a meal saved a moment ago is in the next read and nothing on disk can go stale.
// Her tables are Parquet, answered by query over the whole log and read whole by a command in the
// sandbox; the text read serves the README alone and refuses a table with what works instead.
// Nothing that writes is overridden, so no writing tool exists on this mount and a write routed here
// is refused with what it can do.
//
// Each person keeps a log of their own, so the mount answers for the person whose turn a call
// serves and nobody else: the account on the principal the call's `_meta` carries. The server's one
// instance has no caller; the registrar asks for a view per call (For). A call that names no account
// — a voice guest's turn, a fire with no person behind it, a call with no context — is refused, never
// shown an empty log, since "she logged nothing" would be a false fact about a health record.
public sealed class SugaryFileSystem(ISugaryLog log, TimeZoneInfo homeZone, ITableEngine engine) : FileSystemBackendBase
{
    public const string Name = "sugary";

    // Whose log this view reads; null on the server's own instance and for a call that names nobody.
    private Account? _reader;

    // A shallow copy with the reader swapped, as HaFileSystem does: the copy carries every dependency
    // this mount was built with, and the log is shared by reference.
    public override FileSystemBackendBase For(FileSystemCaller caller)
    {
        var view = (SugaryFileSystem)MemberwiseClone();
        view._reader = Principal.Of(caller.Conversation)?.Account;
        return view;
    }

    public override string FilesystemName => Name;

    public override string DescribeMount =>
        "Her carb-counting log from Sugary (she has type 1 diabetes), read-only and always current, as "
        + "Parquet tables: meals/<YYYY-MM>.parquet (one row per meal), items/<YYYY-MM>.parquet (one row per "
        + "food in a meal), doses/<YYYY-MM>.parquet (one row per insulin dose she recorded taking), "
        + "dose-meals.parquet (which meals each dose was for), "
        + "exercise/<YYYY-MM>.parquet (one row per session of exercise she recorded), foods.parquet (the "
        + "foods she keeps), periods.parquet (her menstrual periods), day-notes.parquet (what she wrote about "
        + "a day), and README.md, which explains every column — read it before reading a number. Ask with "
        + "query: tables meals, items, doses, dose_meals, exercise, foods, periods and day_notes, every month at once. "
        + "A table is not read as text; a command in the sandbox reads the files with duckdb or polars. "
        + $"Times are on the home's clock ({homeZone.Id}), each with a `<column>_local` wall time beside it.";

    public override string DescribeRead =>
        "Read README.md, which explains every table and column of her carb log. The tables are Parquet "
        + "and are not read as text: ask them with query.";

    public override string DescribeInfo => "Get info about a path of her carb log: exists, isDirectory, size.";

    public override string DescribeGlob =>
        "List the files of her carb log matching a glob under basePath — one meals and one items table "
        + "per month she logged a meal in, one doses table per month she recorded a dose in, one exercise "
        + "table per month she started a session in. "
        + "`*` matches one path segment, `**` recurses, `?` one char, "
        + "`{a,b}` brace alternation. A trailing slash (e.g. `*/`) lists directories only.";

    public override string DescribeQuery =>
        "Answer one DuckDB SQL statement over her whole log, every month at once: tables meals, items, "
        + "doses, dose_meals, exercise, foods, periods and day_notes, with the columns README.md explains. Every time "
        + "column has a `<column>_local` sibling on the home's clock.";

    public override string DescribeBlobRead =>
        "Read a chunk of a file of her carb log as raw bytes (base64): a table's Parquet, or the README.";

    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // What a call that names nobody is answered, whatever it asked: the log is a person's, and this
    // call is for none.
    private const string ForNobody =
        "This turn acts for nobody signed in, so it can read nobody's Sugary log: /sugary answers only "
        + "for the person whose turn it is, and a guest or a turn with no person behind it has none.";

    private static FsResult<T> Nobody<T>() where T : class => Fail<T>(ToolError.Codes.PermissionDenied, ForNobody);

    public override async Task<FsResult<FsReadResult>> ReadAsync(
        string path, int? offset, int? limit, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<FsReadResult>();
        }

        var node = SugaryPath.Parse(path);
        if (node.IsDirectory)
        {
            return Invalid<FsReadResult>($"'{path}' is a directory; glob it to see its files.");
        }

        if (node.Kind == SugaryNodeKind.Readme)
        {
            return new FsResult<FsReadResult>.Ok(Window(path, SugaryReadme.For(homeZone), offset, limit));
        }

        return await TableAsync(reader, node, ct) is null
            ? NotFound<FsReadResult>(path)
            : FsError.QueryOnly<FsReadResult>(FileSystemResolution.ToVirtualPath(MountPoint, path), MountPoint);
    }

    // What a command in the sandbox reads, through the bridge, which asks a mount for its bytes first.
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            throw new FileSystemOperationException(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.PermissionDenied,
                Message = ForNobody
            });
        }

        var bytes = await BytesAsync(reader, SugaryPath.Parse(path), ct);
        if (bytes is null)
        {
            throw new FileSystemOperationException(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.NotFound,
                Message = $"Path not found: {path}"
            });
        }

        yield return bytes;
    }

    // The byte read as the wire asks it. A refusal thrown from the stream above would reach the wire
    // as a failure to retry, so a call that names nobody is answered with the refusal itself here.
    public override Task<FsResult<FsBlobReadResult>> ReadBlobAsync(
        string path, long offset, int length, CancellationToken ct) =>
        _reader is null
            ? Task.FromResult(Nobody<FsBlobReadResult>())
            : base.ReadBlobAsync(path, offset, length, ct);

    // A table's size is its Parquet's, rendered to be measured: a reader of Parquet seeks to the end
    // of the file for its footer, so an unknown size would read as a broken file.
    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<FsInfoResult>();
        }

        var node = SugaryPath.Parse(path);
        var bytes = node.IsDirectory ? null : await BytesAsync(reader, node, ct);
        var exists = node.IsDirectory || bytes is not null;
        return new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = exists,
            Path = path,
            IsDirectory = exists ? node.IsDirectory : null,
            Size = bytes?.Length
        });
    }

    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<FsGlobResult>();
        }

        if (!GlobPrologue(basePath, pattern).TryGetValue(out var scope, out var invalidPattern))
        {
            return new FsResult<FsGlobResult>.Err(invalidPattern);
        }

        var (dirsOnly, matches) = scope;
        var nodes = await NodesAsync(reader, ct);
        return Glob(pattern, () => nodes
            .Where(node => node.Kind != SugaryNodeKind.Root && (!dirsOnly || node.IsDirectory))
            .Where(node => matches(node.Path.TrimStart('/')))
            .Select(node => node.IsDirectory ? node.Path + "/" : node.Path)
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList());
    }

    // Whichever path inside the mount it was asked at, a statement sees the whole log: a month is
    // where a file is filed, not a limit on a question.
    public override async Task<FsResult<FsQueryResult>> QueryAsync(
        string path, string sql, int maxRows, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<FsQueryResult>();
        }

        var result = await engine.QueryAsync(await TablesAsync(reader, ct), homeZone, sql, maxRows, ct);
        return result.TryGetValue(out var answer, out var error)
            ? new FsResult<FsQueryResult>.Ok(answer with { Path = path })
            : new FsResult<FsQueryResult>.Err(error.ErrorCode == ToolError.Codes.InvalidArgument
                ? error with { Hint = $"{MountPoint}/README.md explains every table and column." }
                : error);
    }

    private async Task<IReadOnlyList<Table>> TablesAsync(Account reader, CancellationToken ct)
    {
        var meals = await log.MealsAsync(reader, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);
        var doses = await log.DosesAsync(reader, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);
        return
        [
            SugaryTables.Meals(meals, homeZone),
            SugaryTables.Items(meals, homeZone),
            SugaryTables.Doses(doses, homeZone),
            SugaryTables.DoseMeals(doses),
            SugaryTables.Exercise(
                await log.ExerciseAsync(reader, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct), homeZone),
            SugaryTables.Foods(await log.FoodsAsync(reader, ct)),
            SugaryTables.Periods(await log.PeriodsAsync(reader, ct)),
            SugaryTables.DayNotes(await log.DayNotesAsync(reader, ct))
        ];
    }

    // Every node that exists right now: the fixed ones, a pair of files for each month she ate in and
    // a doses file for each month she took a dose in and an exercise file for each month she started
    // a session in — the sets of months independent, so a month of basal alone still has its file.
    private async Task<IReadOnlyList<SugaryNode>> NodesAsync(Account reader, CancellationToken ct)
    {
        var mealMonths = Months(await log.MealTimesAsync(reader, ct));
        var doseMonths = Months(await log.DoseTimesAsync(reader, ct));
        var exerciseMonths = Months(await log.ExerciseTimesAsync(reader, ct));
        SugaryNode[] fixedNodes =
        [
            new(SugaryNodeKind.Root), new(SugaryNodeKind.Readme), new(SugaryNodeKind.Foods),
            new(SugaryNodeKind.Periods), new(SugaryNodeKind.DayNotes), new(SugaryNodeKind.DoseMeals),
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

    // The file's bytes, or null where there is no such file.
    private async Task<byte[]?> BytesAsync(Account reader, SugaryNode node, CancellationToken ct) =>
        node.Kind == SugaryNodeKind.Readme
            ? _utf8.GetBytes(SugaryReadme.For(homeZone))
            : await TableAsync(reader, node, ct) is { } table ? await engine.ParquetAsync(table, ct) : null;

    // The table a file holds, or null where there is no such file: a month with nothing logged in it
    // has none.
    private async Task<Table?> TableAsync(Account reader, SugaryNode node, CancellationToken ct) => node.Kind switch
    {
        SugaryNodeKind.Foods => SugaryTables.Foods(await log.FoodsAsync(reader, ct)),
        SugaryNodeKind.Periods => SugaryTables.Periods(await log.PeriodsAsync(reader, ct)),
        SugaryNodeKind.DayNotes => SugaryTables.DayNotes(await log.DayNotesAsync(reader, ct)),
        SugaryNodeKind.DoseMeals => SugaryTables.DoseMeals(
            await log.DosesAsync(reader, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct)),
        SugaryNodeKind.MealsMonth => await MealsInAsync(reader, node.Month!, ct) is { Count: > 0 } meals
            ? SugaryTables.Meals(meals, homeZone)
            : null,
        SugaryNodeKind.ItemsMonth => await MealsInAsync(reader, node.Month!, ct) is { Count: > 0 } meals
            ? SugaryTables.Items(meals, homeZone)
            : null,
        SugaryNodeKind.DosesMonth => await InMonthAsync(
                node.Month!, (from, to, c) => log.DosesAsync(reader, from, to, c), dose => dose.TakenAt, ct)
            is { Count: > 0 } doses
            ? SugaryTables.Doses(doses, homeZone)
            : null,
        SugaryNodeKind.ExerciseMonth => await InMonthAsync(
                node.Month!, (from, to, c) => log.ExerciseAsync(reader, from, to, c), session => session.StartedAt, ct)
            is { Count: > 0 } sessions
            ? SugaryTables.Exercise(sessions, homeZone)
            : null,
        _ => null
    };

    // A month on the home's calendar starts and ends at an offset that can differ from UTC's by up
    // to a day either way, so the store is asked for a day more each side and the home's calendar
    // decides which meals are in. The calendar's first and last months have no day beyond them to
    // ask for, and no meal in them, so they are simply empty.
    private Task<IReadOnlyList<LoggedMeal>> MealsInAsync(Account reader, string month, CancellationToken ct) =>
        InMonthAsync(month, (from, to, c) => log.MealsAsync(reader, from, to, c), meal => meal.EatenAt, ct);

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