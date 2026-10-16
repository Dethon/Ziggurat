using System.Globalization;

namespace Domain.Tools.Sugary.Vfs;

public enum SugaryNodeKind
{
    Root, Readme, Foods, MealsDir, ItemsDir, DosesDir, MealsMonth, ItemsMonth, DosesMonth, Unknown
}

// Month is "yyyy-MM" on the monthly kinds and null elsewhere.
public sealed record SugaryNode(SugaryNodeKind Kind, string? Month = null)
{
    public bool IsDirectory =>
        Kind is SugaryNodeKind.Root or SugaryNodeKind.MealsDir or SugaryNodeKind.ItemsDir or SugaryNodeKind.DosesDir;

    // The path a listing or a search hit names this node by, mount-root-relative with a leading slash.
    public string Path => Kind switch
    {
        SugaryNodeKind.Root => "/",
        SugaryNodeKind.Readme => "/" + SugaryPath.ReadmeFileName,
        SugaryNodeKind.Foods => "/" + SugaryPath.FoodsFileName,
        SugaryNodeKind.MealsDir => "/" + SugaryPath.MealsDirName,
        SugaryNodeKind.ItemsDir => "/" + SugaryPath.ItemsDirName,
        SugaryNodeKind.DosesDir => "/" + SugaryPath.DosesDirName,
        SugaryNodeKind.MealsMonth => $"/{SugaryPath.MealsDirName}/{Month}.csv",
        SugaryNodeKind.ItemsMonth => $"/{SugaryPath.ItemsDirName}/{Month}.csv",
        SugaryNodeKind.DosesMonth => $"/{SugaryPath.DosesDirName}/{Month}.csv",
        _ => ""
    };

    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];
}

public static class SugaryPath
{
    public const string ReadmeFileName = "README.md";
    public const string FoodsFileName = "foods.csv";
    public const string MealsDirName = "meals";
    public const string ItemsDirName = "items";
    public const string DosesDirName = "doses";

    public static SugaryNode Parse(string? path)
    {
        var segments = (path ?? "").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments switch
        {
            [] => new SugaryNode(SugaryNodeKind.Root),
            [ReadmeFileName] => new SugaryNode(SugaryNodeKind.Readme),
            [FoodsFileName] => new SugaryNode(SugaryNodeKind.Foods),
            [MealsDirName] => new SugaryNode(SugaryNodeKind.MealsDir),
            [ItemsDirName] => new SugaryNode(SugaryNodeKind.ItemsDir),
            [DosesDirName] => new SugaryNode(SugaryNodeKind.DosesDir),
            [MealsDirName, var file] when MonthOf(file) is { } month => new SugaryNode(SugaryNodeKind.MealsMonth, month),
            [ItemsDirName, var file] when MonthOf(file) is { } month => new SugaryNode(SugaryNodeKind.ItemsMonth, month),
            [DosesDirName, var file] when MonthOf(file) is { } month => new SugaryNode(SugaryNodeKind.DosesMonth, month),
            _ => new SugaryNode(SugaryNodeKind.Unknown)
        };
    }

    // "2026-10.csv" names October 2026; anything else names nothing.
    private static string? MonthOf(string file) =>
        file.EndsWith(".csv", StringComparison.Ordinal)
        && DateTime.TryParseExact(file[..^4], "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? file[..^4]
            : null;
}