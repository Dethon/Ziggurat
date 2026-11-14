using System.Globalization;
using System.Text;

namespace Domain.Tools.Sugary.Vfs;

public enum SugaryNodeKind
{
    Root, People, Readme, Foods, Periods, DayNotes, DoseMeals, MealsDir, ItemsDir, DosesDir, ExerciseDir, MealsMonth, ItemsMonth, DosesMonth,
    ExerciseMonth, Unknown
}

// Month is "yyyy-MM" on the monthly kinds and null elsewhere.
public sealed record SugaryNode(SugaryNodeKind Kind, string? Month = null)
{
    public bool IsDirectory =>
        Kind is SugaryNodeKind.Root or SugaryNodeKind.People or SugaryNodeKind.MealsDir or SugaryNodeKind.ItemsDir or SugaryNodeKind.DosesDir
            or SugaryNodeKind.ExerciseDir;

    // The path a listing or a search hit names this node by, mount-root-relative with a leading slash.
    public string Path => Kind switch
    {
        SugaryNodeKind.Root => "/",
        SugaryNodeKind.People => "/" + SugaryPath.PeopleDirName,
        SugaryNodeKind.Readme => "/" + SugaryPath.ReadmeFileName,
        SugaryNodeKind.Foods => "/" + SugaryPath.FoodsFileName,
        SugaryNodeKind.Periods => "/" + SugaryPath.PeriodsFileName,
        SugaryNodeKind.DayNotes => "/" + SugaryPath.DayNotesFileName,
        SugaryNodeKind.DoseMeals => "/" + SugaryPath.DoseMealsFileName,
        SugaryNodeKind.MealsDir => "/" + SugaryPath.MealsDirName,
        SugaryNodeKind.ItemsDir => "/" + SugaryPath.ItemsDirName,
        SugaryNodeKind.DosesDir => "/" + SugaryPath.DosesDirName,
        SugaryNodeKind.ExerciseDir => "/" + SugaryPath.ExerciseDirName,
        SugaryNodeKind.MealsMonth => $"/{SugaryPath.MealsDirName}/{Month}{SugaryPath.TableExtension}",
        SugaryNodeKind.ItemsMonth => $"/{SugaryPath.ItemsDirName}/{Month}{SugaryPath.TableExtension}",
        SugaryNodeKind.DosesMonth => $"/{SugaryPath.DosesDirName}/{Month}{SugaryPath.TableExtension}",
        SugaryNodeKind.ExerciseMonth => $"/{SugaryPath.ExerciseDirName}/{Month}{SugaryPath.TableExtension}",
        _ => ""
    };
}

// Person is the folder's name under people/ (null: the reader's own log), Rest the path inside that log.
public sealed record SugaryLocation(string? Person, string Rest, bool IsPeople = false);

public static class SugaryPath
{
    public const string ReadmeFileName = "README.md";
    public const string TableExtension = ".parquet";
    public const string FoodsFileName = "foods" + TableExtension;
    public const string PeriodsFileName = "periods" + TableExtension;
    public const string DayNotesFileName = "day-notes" + TableExtension;
    public const string DoseMealsFileName = "dose-meals" + TableExtension;
    public const string MealsDirName = "meals";
    public const string ItemsDirName = "items";
    public const string DosesDirName = "doses";
    public const string ExerciseDirName = "exercise";

    // Where everyone else's logs are, for a reader an owner let read them: people/<name>/ holds the same
    // files as the root does for the reader's own.
    public const string PeopleDirName = "people";

    // Whose log a path is in: the reader's own (Person null), the people directory itself, or the
    // person a folder under it names, with the rest of the path inside their log.
    public static SugaryLocation Locate(string? path)
    {
        var segments = Segments(path);
        return segments switch
        {
            [PeopleDirName] => new SugaryLocation(null, "/", IsPeople: true),
            [PeopleDirName, var person, .. var rest] => new SugaryLocation(person, "/" + string.Join('/', rest)),
            _ => new SugaryLocation(null, path ?? "/")
        };
    }

    // A display name as a folder under people/: its letters and digits lowercased, every other run of
    // characters one hyphen, so no name can climb out of its folder or split it in two.
    public static string FolderOf(string displayName)
    {
        var folder = new StringBuilder();
        foreach (var c in displayName.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                folder.Append(c);
            }
            else if (folder.Length > 0 && folder[^1] != '-')
            {
                folder.Append('-');
            }
        }

        var trimmed = folder.ToString().TrimEnd('-');
        return trimmed.Length == 0 ? "person" : trimmed;
    }

    private static string[] Segments(string? path) =>
        (path ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static SugaryNode Parse(string? path)
    {
        var segments = Segments(path);

        return segments switch
        {
            [] => new SugaryNode(SugaryNodeKind.Root),
            [ReadmeFileName] => new SugaryNode(SugaryNodeKind.Readme),
            [FoodsFileName] => new SugaryNode(SugaryNodeKind.Foods),
            [PeriodsFileName] => new SugaryNode(SugaryNodeKind.Periods),
            [DayNotesFileName] => new SugaryNode(SugaryNodeKind.DayNotes),
            [DoseMealsFileName] => new SugaryNode(SugaryNodeKind.DoseMeals),
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

    // "2026-10.parquet" names October 2026; anything else names nothing.
    private static string? MonthOf(string file) =>
        file.EndsWith(TableExtension, StringComparison.Ordinal)
        && DateTime.TryParseExact(file[..^TableExtension.Length], "yyyy-MM", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _)
            ? file[..^TableExtension.Length]
            : null;
}