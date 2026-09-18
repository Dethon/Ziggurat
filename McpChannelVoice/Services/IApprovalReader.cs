using Domain.DTOs.Metrics;

namespace McpChannelVoice.Services;

// Reads a spoken approval answer for what it means. The tool asks this and nothing else about
// the transcript; what stands behind it — a judge, a word list, both — is the reader's business.
public interface IApprovalReader
{
    Task<ApprovalReading> ReadAsync(string prompt, string answer, CancellationToken ct);
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

public sealed record ApprovalReading(
    ApprovalResponse Response,
    ApprovalDecider DecidedBy,
    double? Approved,
    double? Declined,
    TimeSpan Latency)
{
    public string DecidedByName => DecidedBy switch
    {
        ApprovalDecider.Judgment => ApprovalDeciders.Judgment,
        ApprovalDecider.Agreement => ApprovalDeciders.Agreement,
        _ => ApprovalDeciders.WordList
    };
}