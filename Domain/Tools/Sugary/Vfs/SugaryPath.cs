using System.Globalization;

namespace Domain.Tools.Sugary.Vfs;

public enum SugaryNodeKind
{
    Root, Readme, Foods, Periods, DayNotes, MealsDir, ItemsDir, DosesDir, ExerciseDir, MealsMonth, ItemsMonth, DosesMonth,
    ExerciseMonth, Unknown
}

// Month is "yyyy-MM" on the monthly kinds and null elsewhere.
public sealed record SugaryNode(SugaryNodeKind Kind, string? Month = null)
{
    public bool IsDirectory =>
        Kind is SugaryNodeKind.Root or SugaryNodeKind.MealsDir or SugaryNodeKind.ItemsDir or SugaryNodeKind.DosesDir
            or SugaryNodeKind.ExerciseDir;

    // The path a listing or a search hit names this node by, mount-root-relative with a leading slash.
    public string Path => Kind switch
    {
        SugaryNodeKind.Root => "/",
        SugaryNodeKind.Readme => "/" + SugaryPath.ReadmeFileName,
        SugaryNodeKind.Foods => "/" + SugaryPath.FoodsFileName,
        SugaryNodeKind.Periods => "/" + SugaryPath.PeriodsFileName,
        SugaryNodeKind.DayNotes => "/" + SugaryPath.DayNotesFileName,
        SugaryNodeKind.MealsDir => "/" + SugaryPath.MealsDirName,
        SugaryNodeKind.ItemsDir => "/" + SugaryPath.ItemsDirName,
        SugaryNodeKind.DosesDir => "/" + SugaryPath.DosesDirName,
        SugaryNodeKind.ExerciseDir => "/" + SugaryPath.ExerciseDirName,
        SugaryNodeKind.MealsMonth => $"/{SugaryPath.MealsDirName}/{Month}.parquet",
        SugaryNodeKind.ItemsMonth => $"/{SugaryPath.ItemsDirName}/{Month}.parquet",
        SugaryNodeKind.DosesMonth => $"/{SugaryPath.DosesDirName}/{Month}.parquet",
        SugaryNodeKind.ExerciseMonth => $"/{SugaryPath.ExerciseDirName}/{Month}.parquet",
        _ => ""
    };

    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];
}

public static class SugaryPath
{
    public const string ReadmeFileName = "README.md";
    public const string FoodsFileName = "foods.parquet";
    public const string PeriodsFileName = "periods.parquet";
    public const string DayNotesFileName = "day-notes.parquet";
    public const string MealsDirName = "meals";
    public const string ItemsDirName = "items";
    public const string DosesDirName = "doses";
    public const string ExerciseDirName = "exercise";

    public static SugaryNode Parse(string? path)
    {
        var segments = (path ?? "").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments switch
        {
            [] => new SugaryNode(SugaryNodeKind.Root),
            [ReadmeFileName] => new SugaryNode(SugaryNodeKind.Readme),
            [FoodsFileName] => new SugaryNode(SugaryNodeKind.Foods),
            [PeriodsFileName] => new SugaryNode(SugaryNodeKind.Periods),
            [DayNotesFileName] => new SugaryNode(SugaryNodeKind.DayNotes),
            [MealsDirName] => new SugaryNode(SugaryNodeKind.MealsDir),
            [ItemsDirName] => new SugaryNode(SugaryNodeKind.ItemsDir),
            [DosesDirName] => new SugaryNode(SugaryNodeKind.DosesDir),
            [ExerciseDirName] => new SugaryNode(SugaryNodeKind.ExerciseDir),
            [MealsDirName, var file] when MonthOf(file) is { } month => new SugaryNode(SugaryNodeKind.MealsMonth, month),
            [ItemsDirName, var file] when MonthOf(file) is { } month => new SugaryNode(SugaryNodeKind.ItemsMonth, month),
            [DosesDirName, var file] when MonthOf(file) is { } month => new SugaryNode(SugaryNodeKind.DosesMonth, month),
            [ExerciseDirName, var file] when MonthOf(file) is { } month =>
                new SugaryNode(SugaryNodeKind.ExerciseMonth, month),
            _ => new SugaryNode(SugaryNodeKind.Unknown)
        };
    }

    private const string TableExtension = ".parquet";

    // "2026-10.parquet" names October 2026; anything else names nothing.
    private static string? MonthOf(string file) =>
        file.EndsWith(TableExtension, StringComparison.Ordinal)
        && DateTime.TryParseExact(file[..^TableExtension.Length], "yyyy-MM", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _)
            ? file[..^TableExtension.Length]
            : null;
}