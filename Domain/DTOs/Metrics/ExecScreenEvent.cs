namespace Domain.DTOs.Metrics;

// One exec screened before it would have run unasked, whatever the judge answered. The bars are
// re-read from these: the four probabilities per call, which way each call went, and — for the
// calls that got no verdict — why, so an outage on the host path is heard as prompts it caused.
public record ExecScreenEvent : MetricEvent
{
    // "contained" or "host": where the command was headed.
    public required string Reach { get; init; }

    // One of ExecScreenOutcomes: ran or asked.
    public required string Outcome { get; init; }

    public IReadOnlyList<string> Codes { get; init; } = [];

    // Null when there was no verdict.
    public double? ServesRequest { get; init; }

    public double? Destroys { get; init; }

    public double? SendsOut { get; init; }

    public double? RunsDownloaded { get; init; }

    // One of ExecScreenAbsences, or null when the judge answered in time.
    public string? AbsenceReason { get; init; }

    public long? DurationMs { get; init; }

    public int? InputTokens { get; init; }

    // What the provider charged for the judgment, off its own usage.
    public decimal? Cost { get; init; }

    public string? Model { get; init; }
}

public static class ExecScreenOutcomes
{
    public const string Ran = "ran";
    public const string Asked = "asked";
}

public static class ExecScreenAbsences
{
    public const string Unconfigured = "unconfigured";
    public const string Deadline = "deadline";
    public const string Error = "error";
    public const string LocalTurn = "local_turn";
}