using Domain.DTOs.Metrics;
using Domain.Judgments;

namespace McpChannelVoice.Services;

// Reads a spoken approval answer for what it means. The tool asks this and nothing else about
// the transcript; what stands behind it — a judge, a word list, both — is the reader's business.
public interface IApprovalReader
{
    // caller is the turn that asked for the approval — its model and who asked — from the call's
    // `_meta`: a reader that asks a hosted judge hands it on, and the judge's client sends nothing
    // for the local box and bills the rest to whoever asked.
    Task<ApprovalReading> ReadAsync(string prompt, string answer, JudgmentCaller caller, CancellationToken ct);
}

public sealed record ApprovalReading(
    ApprovalResponse Response,
    ApprovalDecider DecidedBy,
    double? Approved,
    double? Declined,
    TimeSpan Latency);