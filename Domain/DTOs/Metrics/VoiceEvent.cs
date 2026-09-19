using Domain.DTOs.Metrics.Enums;

namespace Domain.DTOs.Metrics;

public record VoiceEvent : MetricEvent
{
    public required VoiceMetric Metric { get; init; }
    // Which channel the speech came through: the satellites, the browser or Telegram. Dictation
    // records the same members from the two chat channels, so this is what separates the three on
    // a dashboard that needs no new metric family to show them.
    public string? Channel { get; init; }
    public string? SatelliteId { get; init; }
    public string? Room { get; init; }
    public string? Identity { get; init; }
    // The enrolled person a report is about, where Identity is the satellite's own configured
    // identity. The two are different questions and one field cannot answer both.
    public string? Speaker { get; init; }
    public string? Outcome { get; init; }
    public string? Priority { get; init; }
    public long? DurationMs { get; init; }
    public double? Confidence { get; init; }
    public double? Similarity { get; init; }
    public string? Error { get; init; }
    public double? PeakRms { get; init; }
    public long? SpeechMs { get; init; }
    public double? FloorRms { get; init; }
    public double? TrailingRms { get; init; }
    public string? EndReason { get; init; }
    public double? AvgLogProb { get; init; }
    public double? NoSpeechProb { get; init; }
    public double? CompressionRatio { get; init; }
    public double? WakeRms { get; init; }
    public double? WakeScore { get; init; }
    // An approval's answer: who read it (one of ApprovalDeciders) and, when the judge answered,
    // its two probabilities. The judge's latency rides DurationMs on the same event.
    public string? DecidedBy { get; init; }
    public double? ApprovedProbability { get; init; }
    public double? DeclinedProbability { get; init; }
}

public enum ApprovalDecider
{
    // The judge was sure, or the judge answered and nothing was sure enough to act.
    Judgment,

    // The judge leaned and the word list leaned the same way.
    Agreement,

    // The judge was absent, late, off, or the answer was empty: the word list alone.
    WordList
}

// The wire spellings of who decided a spoken approval, the ones a dashboard groups by.
public static class ApprovalDeciders
{
    public const string Judgment = "judgment";
    public const string Agreement = "agreement";
    public const string WordList = "wordlist";

    public static string Of(ApprovalDecider decider) => decider switch
    {
        ApprovalDecider.Judgment => Judgment,
        ApprovalDecider.Agreement => Agreement,
        ApprovalDecider.WordList => WordList,
        _ => throw new ArgumentOutOfRangeException(nameof(decider), decider, null)
    };
}