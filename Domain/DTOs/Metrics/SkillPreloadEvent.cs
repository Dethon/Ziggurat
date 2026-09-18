namespace Domain.DTOs.Metrics;

// One judgment asked of Jev about which skills a request needs, whatever it answered. Together
// these are what an operator reads a TypeSafe degradation off: the share of turns that got a
// head start, the share that timed out, and how long the judge took. A skipped Lemonade turn is
// one too, carrying no latency, because the gate is a decision worth counting.
public record SkillPreloadEvent : MetricEvent
{
    // One of SkillPreloadOutcomes: preloaded, abstained, none, deadline, error, skipped-lemonade.
    public required string Outcome { get; init; }

    public string? Channel { get; init; }

    public IReadOnlyList<string> Skills { get; init; } = [];

    // The files read beside the skills, by path: what the model was spared asking for.
    public IReadOnlyList<string> Reads { get; init; } = [];

    public string? Choice { get; init; }

    public double? ChoiceConfidence { get; init; }

    public IReadOnlyDictionary<string, double>? Needs { get; init; }

    public long? DurationMs { get; init; }

    public int? InputTokens { get; init; }

    public string? Model { get; init; }
}

// The wire spellings of an outcome, the ones the dashboard groups by and the spec names.
public static class SkillPreloadOutcomes
{
    public const string Preloaded = "preloaded";
    public const string Abstained = "abstained";
    public const string None = "none";
    public const string Deadline = "deadline";
    public const string Error = "error";
    public const string SkippedLemonade = "skipped-lemonade";
}