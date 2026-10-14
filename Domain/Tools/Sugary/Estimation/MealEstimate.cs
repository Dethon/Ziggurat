namespace Domain.Tools.Sugary.Estimation;

// The standalone Sugary app's estimate, carried over as it was (Sugary/Estimation/MealEstimate.cs):
// what the model says about each item, and the carb figures this side derives from it.

public enum Confidence
{
    Low,
    Medium,
    High
}

public record Interval(double Low, double Best, double High)
{
    public static Interval Zero { get; } = new(0, 0, 0);

    public static Interval Ordered(double a, double b, double c)
    {
        var sorted = new[] { a, b, c }.Select(v => Math.Max(0, v)).Order().ToArray();
        return new Interval(sorted[0], sorted[1], sorted[2]);
    }

    public Interval Times(double factor) => new(Round(Low * factor), Round(Best * factor), Round(High * factor));

    public Interval WidenedTo(double lowFactor, double highFactor) =>
        new(Math.Min(Low, Round(Best * lowFactor)), Best, Math.Max(High, Round(Best * highFactor)));

    public Interval Plus(Interval other) =>
        new(Round(Low + other.Low), Round(Best + other.Best), Round(High + other.High));

    private static double Round(double value) => Math.Round(value, 1);
}

public record EstimatedItem(
    string Name,
    string Portion,
    Interval Grams,
    double CarbsPer100g,
    Interval Carbs,
    Confidence Confidence,
    string? Note);

public record ClarifyingQuestion(string Text, IReadOnlyList<string> Options);

public record MealEstimate(
    IReadOnlyList<EstimatedItem> Items,
    Interval Total,
    IReadOnlyList<ClarifyingQuestion> Questions,
    IReadOnlyList<string> Notes)
{
    // The carb figures are derived here rather than asked of the model, so the total always
    // equals the sum of what is shown and stays consistent when a portion is edited later.
    public static MealEstimate From(ModelEstimate raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var items = raw.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(ToItem)
            .ToList();
        var questions = raw.Questions
            .Where(q => !string.IsNullOrWhiteSpace(q.Text))
            .Select(q => new ClarifyingQuestion(q.Text.Trim(), [.. q.Options.Where(o => o.Length > 0)]))
            .ToList();
        var notes = raw.Notes.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();

        return new MealEstimate(items, items.Aggregate(Interval.Zero, (sum, i) => sum.Plus(i.Carbs)), questions, notes);
    }

    // How far off a portion judged by eye can be. On 64 photographed meals of known carbs the
    // ranges the model gave held the true value about half the time, whichever model was asked
    // and however the prompt asked for wider ones; with this floor it was about three in five.
    private const double EyeballedLow = 0.6;
    private const double EyeballedHigh = 1.6;

    private static EstimatedItem ToItem(ModelItem item)
    {
        var stated = Interval.Ordered(item.GramsLow, item.Grams, item.GramsHigh);
        // High confidence is how the model marks an amount the person stated, which is kept as given.
        var grams = item.Confidence == Confidence.High ? stated : stated.WidenedTo(EyeballedLow, EyeballedHigh);
        var density = Math.Clamp(item.CarbsPer100g, 0, 100);
        return new EstimatedItem(
            item.Name.Trim(),
            item.Portion.Trim(),
            grams,
            density,
            grams.Times(density / 100),
            item.Confidence,
            string.IsNullOrWhiteSpace(item.Note) ? null : item.Note.Trim());
    }
}

// What the model answers, field for field as the schema asks for it. Every member is required and
// nothing is optional: a reply is read whole or refused (MealEstimateReply).
public record ModelItem(
    string Name,
    string Portion,
    double GramsLow,
    double Grams,
    double GramsHigh,
    double CarbsPer100g,
    Confidence Confidence,
    string? Note);

public record ModelQuestion(string Text, IReadOnlyList<string> Options);

public record ModelEstimate(
    IReadOnlyList<ModelItem> Items,
    IReadOnlyList<ModelQuestion> Questions,
    IReadOnlyList<string> Notes);