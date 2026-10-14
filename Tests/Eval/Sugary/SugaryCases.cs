using System.Text.Json;
using Tests.Eval.Harness;

namespace Tests.Eval.Sugary;

// What reached the model: photos alone, or a clip (with the photos beside it). The scorecard keeps
// the two apart, because the gain video bought on the standalone benchmark came from the meals it
// was filmed for and would be hidden inside an average over both.
public enum SugaryInput
{
    Photo,
    Video
}

// One benchmark meal with known carbs, as the standalone Sugary benchmark wrote it
// (eval/external/cases.json there, copied here unchanged; video-cases.json films its 14 Nutrition5k
// dishes again, from the same dataset, with the same truth). `Carbs` is the truth the estimate is
// scored against: available carbohydrate with fibre excluded. The README beside the file says how
// each set's truth was obtained and what that means for its score.
public sealed record SugaryCase
{
    public required string Name { get; init; }

    public required string Set { get; init; }

    // Relative to the cases file. Not in git: the photos are fetched and placed by hand.
    public IReadOnlyList<string> Photos { get; init; } = [];

    // A short film around the plate, relative to the cases file (video-cases.json's cases).
    public string? Clip { get; init; }

    // Stills of the same plate the clip stands for, sent instead of it to a model that only sees
    // pictures, as the Sugary channel sends the stills it takes from a clip.
    public IReadOnlyList<string> Stills { get; init; } = [];

    // What the person typed. Null on every public case: the photo is what is measured.
    public string? Description { get; init; }

    public string? Hint { get; init; }

    public double Carbs { get; init; }

    // The published figure before fibre was subtracted, kept so the subtraction can be checked.
    public double? TotalCarbs { get; init; }

    // The edible weight on the plate, which the estimate's weights are compared with.
    public double? Grams { get; init; }

    // Every food with its weight, as the dataset recorded it.
    public string? Truth { get; init; }

    public SugaryInput Input => Clip is null ? SugaryInput.Photo : SugaryInput.Video;
}

public static class SugaryCases
{
    // Beside this code, in the working tree: the case files travel with the tier, and only the
    // photos they name stay out of git.
    public static string Directory { get; } =
        Path.Combine(RepositoryRoot.Path, "Tests", "Eval", "Sugary");

    public static string PhotoDirectory => Path.Combine(Directory, "photos");

    // The photographed meals, then the filmed ones.
    private static readonly Lazy<IReadOnlyList<SugaryCase>> _shipped =
        new(() =>
        [
            .. Parse(File.ReadAllText(Path.Combine(Directory, "cases.json"))),
            .. Parse(File.ReadAllText(Path.Combine(Directory, "video-cases.json")))
        ]);

    public static IReadOnlyList<SugaryCase> Shipped => _shipped.Value;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // Refused whole when a case has no truth: a missing `carbs` would read as zero, and a meal
    // scored against zero grams is an error the size of the meal that nothing would explain.
    public static IReadOnlyList<SugaryCase> Parse(string json)
    {
        var rows = JsonSerializer.Deserialize<List<Row>>(json, _json) ?? [];
        var unscorable = rows.Where(row => row.Carbs is null || string.IsNullOrWhiteSpace(row.Name)
                                                             || string.IsNullOrWhiteSpace(row.Set)).ToList();
        if (unscorable.Count > 0)
        {
            throw new InvalidDataException(
                "these cases have no name, set or carbs to score against: "
                + string.Join(", ", unscorable.Select(row => row.Name ?? "(unnamed)")));
        }

        return
        [
            .. rows.Select(row => new SugaryCase
            {
                Name = row.Name!,
                Set = row.Set!,
                Photos = row.Photos ?? [],
                Clip = row.Clip,
                Stills = row.Stills ?? [],
                Description = row.Description,
                Hint = row.Hint,
                Carbs = row.Carbs!.Value,
                TotalCarbs = row.TotalCarbs,
                Grams = row.Grams,
                Truth = row.Truth
            })
        ];
    }

    private sealed record Row(
        string? Name, string? Set, IReadOnlyList<string>? Photos, string? Clip, IReadOnlyList<string>? Stills,
        string? Description,
        string? Hint, double? Carbs, double? TotalCarbs, double? Grams, string? Truth);
}