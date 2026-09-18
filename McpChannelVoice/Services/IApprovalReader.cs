using Domain.DTOs.Metrics;

namespace McpChannelVoice.Services;

// Reads a spoken approval answer for what it means. The tool asks this and nothing else about
// the transcript; what stands behind it — a judge, a word list, both — is the reader's business.
public interface IApprovalReader
{
    Task<ApprovalReading> ReadAsync(string prompt, string answer, CancellationToken ct);
}

public sealed record ApprovalReading(
    ApprovalResponse Response,
    ApprovalDecider DecidedBy,
    double? Approved,
    double? Declined,
    TimeSpan Latency);