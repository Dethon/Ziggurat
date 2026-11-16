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
// serves: the account on the principal the call's `_meta` carries. The server's one instance has no
// caller; the registrar asks for a view per call (For). A call that names no account — a voice
// guest's turn, a fire with no person behind it, a call with no context — is refused, never shown an
// empty log, since "she logged nothing" would be a false fact about a health record.
//
// A reader an owner let read everyone's Sugary log (the members page) also finds each other person of
// the household under people/<name>/, with the same files. Who holds that grant is the household's
// people view, asked through the household the call proved, and only a reader it lists as granted, of
// that very household, sees people/; anyone else asking into it is refused, never shown an empty log.
public sealed class SugaryFileSystem(ISugaryLog log, TimeZoneInfo serverZone, ITableEngine engine) : FileSystemBackendBase
{
    public const string Name = "sugary";

    // Whose log this view reads; null on the server's own instance and for a call that names nobody.
    private Account? _reader;

    // The reader's household as the call proved it, whose settings say the clock her times are read
    // on: the household's zone, or the server's own for a household this host cannot place.
    private ICallHousehold? _household;

    // A shallow copy with the reader swapped, as HaFileSystem does: the copy carries every dependency
    // this mount was built with, and the log is shared by reference.
    public override FileSystemBackendBase For(FileSystemCaller caller)
    {
        var view = (SugaryFileSystem)MemberwiseClone();
        view._reader = Principal.Of(caller.Conversation)?.Account;
        view._household = caller.Household;
        return view;
    }

    public override string FilesystemName => Name;

    public override string DescribeMount =>
        "The carb-counting log from Sugary of the person the turn is for, read-only and always current, as "
        + "Parquet tables: meals/<YYYY-MM>.parquet (one row per meal), items/<YYYY-MM>.parquet (one row per "
        + "food in a meal), doses/<YYYY-MM>.parquet (one row per insulin dose they recorded taking), "
        + "dose-meals.parquet (which meals each dose was for), "
        + "exercise/<YYYY-MM>.parquet (one row per session of exercise they recorded), foods.parquet (the "
        + "foods they keep), periods.parquet (their menstrual periods), day-notes.parquet (what they wrote about "
        + "a day), and README.md, which explains every column — read it before reading a number. Ask with "
        + "query: tables meals, items, doses, dose_meals, exercise, foods, periods and day_notes, every month at once. "
        + "A table is not read as text; a command in the sandbox reads the files with duckdb or polars. "
        + "Times are instants, each with a `<column>_local` wall time beside it in local time (README.md names the zone). "
        + "Another person's log is not here, unless an owner of the household let this person read everyone's: "
        + "then each other person's log is under people/<name>/ with the same files, and a query asked at "
        + "people/<name> answers over theirs.";

    public override string DescribeRead =>
        "Read README.md, which explains every table and column of the carb log. The tables are Parquet "
        + "and are not read as text: ask them with query.";

    public override string DescribeInfo => "Get info about a path of the carb log: exists, isDirectory, size.";

    public override string DescribeGlob =>
        "List the files of the carb log matching a glob under basePath — one meals and one items table "
        + "per month a meal was logged in, one doses table per month a dose was recorded in, one exercise "
        + "table per month a session was started in. "
        + "`*` matches one path segment, `**` recurses, `?` one char, "
        + "`{a,b}` brace alternation. A trailing slash (e.g. `*/`) lists directories only.";

    public override string DescribeQuery =>
        "Answer one DuckDB SQL statement over one person's whole log, every month at once: tables meals, items, "
        + "doses, dose_meals, exercise, foods, periods and day_notes, with the columns README.md explains. Every time "
        + "column has a `<column>_local` sibling on the home's clock. Asked anywhere outside people/, it reads the "
        + "turn's person's own log; asked at or under people/<name>, that person's.";

    public override string DescribeBlobRead =>
        "Read a chunk of a file of the carb log as raw bytes (base64): a table's Parquet, or the README.";

    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // What a call that names nobody is answered, whatever it asked: the log is a person's, and this
    // call is for none.
    private const string ForNobody =
        "This turn acts for nobody signed in, so it can read nobody's Sugary log: /sugary answers only "
        + "for the person whose turn it is, and a guest or a turn with no person behind it has none.";

    private static FsResult<T> Nobody<T>() where T : class => Fail<T>(ToolError.Codes.PermissionDenied, ForNobody);

    // What a reader without the grant is answered for anything under people/.
    private const string NotGranted =
        "This turn's person may read only their own Sugary log: other people's logs are under people/ only for "
        + "someone an owner of the household lets read everyone's Sugary log, on the household's members page.";

    // Another person's log as this reader may open it: their folder under people/, and their account,
    // always of the reader's own household.
    private sealed record OtherLog(string Folder, Account Owner);

    // Whose log a path is in, and which node of it.
    private sealed record Target(Account Owner, SugaryNode Node);

    public override async Task<FsResult<FsReadResult>> ReadAsync(
        string path, int? offset, int? limit, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<FsReadResult>();
        }

        if (!(await ResolveAsync(reader, path, ct)).TryGetValue(out var target, out var refused))
        {
            return new FsResult<FsReadResult>.Err(refused);
        }

        if (target.Node.IsDirectory)
        {
            return Invalid<FsReadResult>($"'{path}' is a directory; glob it to see its files.");
        }

        if (target.Node.Kind == SugaryNodeKind.Readme)
        {
            return new FsResult<FsReadResult>.Ok(Window(path, SugaryReadme.For(await ZoneAsync(ct)), offset, limit));
        }

        return await TableAsync(target.Owner, target.Node, await ZoneAsync(ct), ct) is null
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

        if (!(await ResolveAsync(reader, path, ct)).TryGetValue(out var target, out var refused))
        {
            throw new FileSystemOperationException(refused);
        }

        var bytes = target.Node.IsDirectory ? null : await BytesAsync(target.Owner, target.Node, await ZoneAsync(ct), ct);
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
    // as a failure to retry, so a call that names nobody, or asks into a log it may not read, is
    // answered with the refusal itself here.
    public override async Task<FsResult<FsBlobReadResult>> ReadBlobAsync(
        string path, long offset, int length, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<FsBlobReadResult>();
        }

        return (await ResolveAsync(reader, path, ct)).TryGetValue(out _, out var refused)
            ? await base.ReadBlobAsync(path, offset, length, ct)
            : new FsResult<FsBlobReadResult>.Err(refused);
    }

    // A table's size is its Parquet's, rendered to be measured: a reader of Parquet seeks to the end
    // of the file for its footer, so an unknown size would read as a broken file.
    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<FsInfoResult>();
        }

        if (!(await ResolveAsync(reader, path, ct)).TryGetValue(out var target, out var refused))
        {
            return refused.ErrorCode == ToolError.Codes.NotFound
                ? new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = false, Path = path })
                : new FsResult<FsInfoResult>.Err(refused);
        }

        var node = target.Node;
        var bytes = node.IsDirectory ? null : await BytesAsync(target.Owner, node, await ZoneAsync(ct), ct);
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

        if (!(await ResolveAsync(reader, basePath, ct)).TryGetValue(out _, out var refused))
        {
            return new FsResult<FsGlobResult>.Err(refused);
        }

        var (dirsOnly, matches) = scope;
        var zone = await ZoneAsync(ct);
        var entries = (await NodesAsync(reader, zone, ct)).Select(node => (node.Path, node.IsDirectory)).ToList();
        if (await OthersAsync(reader, ct) is { } others)
        {
            entries.Add(("/" + SugaryPath.PeopleDirName, true));
            foreach (var other in others)
            {
                var prefix = PrefixOf(other);
                entries.AddRange((await NodesAsync(other.Owner, zone, ct)).Select(node =>
                    (node.Kind == SugaryNodeKind.Root ? prefix : prefix + node.Path, node.IsDirectory)));
            }
        }

        return Glob(pattern, () => entries
            .Where(entry => entry.Path != "/" && (!dirsOnly || entry.IsDirectory))
            .Where(entry => matches(entry.Path.TrimStart('/')))
            .Select(entry => entry.IsDirectory ? entry.Path + "/" : entry.Path)
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList());
    }

    // Whichever path inside a person's log it was asked at, a statement sees that whole log: a month is
    // where a file is filed, not a limit on a question. Whose log is the path's: under people/<name>,
    // that person's; anywhere else, the reader's own.
    public override async Task<FsResult<FsQueryResult>> QueryAsync(
        string path, string sql, int maxRows, CancellationToken ct)
    {
        if (_reader is not { } reader)
        {
            return Nobody<FsQueryResult>();
        }

        if (!(await ResolveAsync(reader, path, ct)).TryGetValue(out var target, out var refused))
        {
            return new FsResult<FsQueryResult>.Err(refused);
        }

        if (target.Node.Kind == SugaryNodeKind.People)
        {
            return Invalid<FsQueryResult>(
                $"{MountPoint}/{SugaryPath.PeopleDirName} holds one folder per person: ask at "
                + $"{MountPoint}/{SugaryPath.PeopleDirName}/<name> for theirs.");
        }

        var zone = await ZoneAsync(ct);
        var result = await engine.QueryAsync(await TablesAsync(target.Owner, zone, ct), zone, sql, maxRows, ct);
        return result.TryGetValue(out var answer, out var error)
            ? new FsResult<FsQueryResult>.Ok(answer with { Path = path })
            : new FsResult<FsQueryResult>.Err(error.ErrorCode == ToolError.Codes.InvalidArgument
                ? error with { Hint = $"{MountPoint}/README.md explains every table and column." }
                : error);
    }

    // Whose log a path is in, as this reader may open it: the reader's own outside people/; under it,
    // only for a reader granted everyone's, the person whose folder it names, or not found naming who
    // is there.
    private async Task<FsResult<Target>> ResolveAsync(Account reader, string? path, CancellationToken ct)
    {
        var location = SugaryPath.Locate(path);
        if (location is { Person: null, IsPeople: false })
        {
            return new FsResult<Target>.Ok(new Target(reader, SugaryPath.Parse(location.Rest)));
        }

        if (await OthersAsync(reader, ct) is not { } others)
        {
            return Fail<Target>(ToolError.Codes.PermissionDenied, NotGranted);
        }

        if (location.IsPeople)
        {
            return new FsResult<Target>.Ok(new Target(reader, new SugaryNode(SugaryNodeKind.People)));
        }

        return others.FirstOrDefault(other => other.Folder == location.Person) is { } found
            ? new FsResult<Target>.Ok(new Target(found.Owner, SugaryPath.Parse(location.Rest)))
            : Fail<Target>(ToolError.Codes.NotFound,
                $"Nobody's log is at {SugaryPath.PeopleDirName}/{location.Person}. The people whose logs are here: "
                + (others.Count == 0 ? "none" : string.Join(", ", others.Select(other => other.Folder))) + ".");
    }

    // Everyone else of the reader's household, each under a folder of their own, when the household the
    // call proved is the reader's and its people view lists the reader as granted everyone's log; null
    // otherwise. Two people whose names make one folder are told apart in the order they joined.
    private async Task<IReadOnlyList<OtherLog>?> OthersAsync(Account reader, CancellationToken ct)
    {
        if (_household is not { } household || household.Id != reader.TenantId)
        {
            return null;
        }

        var people = (await household.SugaryPeopleAsync(ct)).People;
        if (!people.Any(person => person.UserId == reader.UserId && person.ReadsEveryLog))
        {
            return null;
        }

        var taken = new HashSet<string>(StringComparer.Ordinal);
        return people
            .Where(person => person.UserId != reader.UserId)
            .Select(person => new OtherLog(FreeFolder(SugaryPath.FolderOf(person.DisplayName), taken),
                new Account(reader.TenantId, person.UserId)))
            .ToList();
    }

    // The folder itself where nobody took it yet, else the first numbered one free.
    private static string FreeFolder(string folder, HashSet<string> taken)
    {
        var candidate = folder;
        for (var n = 2; !taken.Add(candidate); n++)
        {
            candidate = $"{folder}-{n}";
        }

        return candidate;
    }

    private static string PrefixOf(OtherLog other) => $"/{SugaryPath.PeopleDirName}/{other.Folder}";

    // Asked once per operation, of the reader the configuration client keeps, and only by an
    // operation that reads a time.
    private Task<TimeZoneInfo> ZoneAsync(CancellationToken ct) => _household.ZoneAsync(serverZone, ct);

    private async Task<IReadOnlyList<Table>> TablesAsync(Account owner, TimeZoneInfo zone, CancellationToken ct)
    {
        var meals = await log.MealsAsync(owner, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);
        var doses = await log.DosesAsync(owner, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);
        return
        [
            SugaryTables.Meals(meals, zone),
            SugaryTables.Items(meals, zone),
            SugaryTables.Doses(doses, zone),
            SugaryTables.DoseMeals(doses),
            SugaryTables.Exercise(
                await log.ExerciseAsync(owner, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct), zone),
            SugaryTables.Foods(await log.FoodsAsync(owner, ct)),
            SugaryTables.Periods(await log.PeriodsAsync(owner, ct)),
            SugaryTables.DayNotes(await log.DayNotesAsync(owner, ct))
        ];
    }

    // Every node that exists right now: the fixed ones, a pair of files for each month she ate in and
    // a doses file for each month she took a dose in and an exercise file for each month she started
    // a session in — the sets of months independent, so a month of basal alone still has its file.
    private async Task<IReadOnlyList<SugaryNode>> NodesAsync(Account reader, TimeZoneInfo zone, CancellationToken ct)
    {
        var mealMonths = Months(await log.MealTimesAsync(reader, ct), zone);
        var doseMonths = Months(await log.DoseTimesAsync(reader, ct), zone);
        var exerciseMonths = Months(await log.ExerciseTimesAsync(reader, ct), zone);
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

    private static IReadOnlyList<string> Months(IEnumerable<DateTimeOffset> times, TimeZoneInfo zone) =>
        times
            .Select(time => SugaryTables.Month(time, zone))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    // The file's bytes, or null where there is no such file.
    private async Task<byte[]?> BytesAsync(Account reader, SugaryNode node, TimeZoneInfo zone, CancellationToken ct) =>
        node.Kind == SugaryNodeKind.Readme
            ? _utf8.GetBytes(SugaryReadme.For(zone))
            : await TableAsync(reader, node, zone, ct) is { } table ? await engine.ParquetAsync(table, ct) : null;

    // The table a file holds, or null where there is no such file: a month with nothing logged in it
    // has none.
    private async Task<Table?> TableAsync(Account reader, SugaryNode node, TimeZoneInfo zone, CancellationToken ct) => node.Kind switch
    {
        SugaryNodeKind.Foods => SugaryTables.Foods(await log.FoodsAsync(reader, ct)),
        SugaryNodeKind.Periods => SugaryTables.Periods(await log.PeriodsAsync(reader, ct)),
        SugaryNodeKind.DayNotes => SugaryTables.DayNotes(await log.DayNotesAsync(reader, ct)),
        SugaryNodeKind.DoseMeals => SugaryTables.DoseMeals(
            await log.DosesAsync(reader, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct)),
        SugaryNodeKind.MealsMonth => await MealsInAsync(reader, node.Month!, zone, ct) is { Count: > 0 } meals
            ? SugaryTables.Meals(meals, zone)
            : null,
        SugaryNodeKind.ItemsMonth => await MealsInAsync(reader, node.Month!, zone, ct) is { Count: > 0 } meals
            ? SugaryTables.Items(meals, zone)
            : null,
        SugaryNodeKind.DosesMonth => await InMonthAsync(
                node.Month!, zone, (from, to, c) => log.DosesAsync(reader, from, to, c), dose => dose.TakenAt, ct)
            is { Count: > 0 } doses
            ? SugaryTables.Doses(doses, zone)
            : null,
        SugaryNodeKind.ExerciseMonth => await InMonthAsync(
                node.Month!, zone, (from, to, c) => log.ExerciseAsync(reader, from, to, c), session => session.StartedAt, ct)
            is { Count: > 0 } sessions
            ? SugaryTables.Exercise(sessions, zone)
            : null,
        _ => null
    };

    // A month on the home's calendar starts and ends at an offset that can differ from UTC's by up
    // to a day either way, so the store is asked for a day more each side and the home's calendar
    // decides which meals are in. The calendar's first and last months have no day beyond them to
    // ask for, and no meal in them, so they are simply empty.
    private Task<IReadOnlyList<LoggedMeal>> MealsInAsync(Account reader, string month, TimeZoneInfo zone, CancellationToken ct) =>
        InMonthAsync(month, zone, (from, to, c) => log.MealsAsync(reader, from, to, c), meal => meal.EatenAt, ct);

    private static async Task<IReadOnlyList<T>> InMonthAsync<T>(
        string month,
        TimeZoneInfo zone,
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
        return rows.Where(row => SugaryTables.Month(when(row), zone) == month).ToList();
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