using Domain.DTOs.Metrics;

namespace McpChannelVoice.Services;

// Reads a spoken approval answer for what it means. The tool asks this and nothing else about
// the transcript; what stands behind it — a judge, a word list, both — is the reader's business.
public interface IApprovalReader
{
    // turnModel is the model the turn that asked for the approval was addressed to, from the
    // call's `_meta`: a reader that asks a hosted judge hands it on, and the judge's client sends
    // nothing for the local box.
    Task<ApprovalReading> ReadAsync(string prompt, string answer, string? turnModel, CancellationToken ct);
}

public sealed record ApprovalReading(
    ApprovalResponse Response,
    ApprovalDecider DecidedBy,
    double? Approved,
    double? Declined,
    TimeSpan Latency);