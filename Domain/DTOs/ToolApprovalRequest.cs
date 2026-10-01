namespace Domain.DTOs;

public record ToolApprovalRequest(
    string? MessageId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments)
{
    // Why the exec screen put this call to the person, as ExecScreenCodes, or null for a request
    // the screen had no part in. A channel renders the codes in its own language beside its
    // existing approval text; a request without them renders exactly as it always did.
    public IReadOnlyList<string>? Screen { get; init; }
}