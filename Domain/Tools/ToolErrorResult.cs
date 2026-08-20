using System.Text.Json.Nodes;

namespace Domain.Tools;

// Typed counterpart to ToolError.Create's JsonObject envelope:
// { ok:false, errorCode, message, retryable, hint? }.
//
// Two of the four fields are derived rather than carried: retryability belongs to the code, and the
// hint falls back to the code's own recovery line. A failure that reaches a model therefore always
// says what to do about it, whether or not the site that raised it remembered to.
public sealed record ToolErrorResult
{
    public required string ErrorCode { get; init; }
    public required string Message { get; init; }

    // What this site knows that the code alone does not. Null falls back to the taxonomy's line.
    public string? Hint { get; init; }

    public bool Retryable => ToolError.IsRetryable(ErrorCode);

    public string? Recovery => string.IsNullOrWhiteSpace(Hint) ? ToolError.Recovery(ErrorCode) : Hint;

    public JsonObject ToNode()
    {
        var obj = new JsonObject
        {
            ["ok"] = false,
            ["errorCode"] = ErrorCode,
            ["message"] = Message,
            ["retryable"] = Retryable
        };

        if (Recovery is { } recovery)
        {
            obj["hint"] = recovery;
        }

        return obj;
    }

    public static bool IsErrorEnvelope(JsonNode? json)
        => json is JsonObject obj
           && obj.TryGetPropertyValue("ok", out var ok)
           && ok is JsonValue v
           && v.TryGetValue<bool>(out var okValue)
           && !okValue;

    // The code is read back and the retryability is not, deliberately: a re-read envelope is
    // answered by the same taxonomy as a fresh one, so a failure crossing a server boundary cannot
    // arrive claiming a retryability this side disagrees with.
    public static ToolErrorResult? FromEnvelope(JsonNode? json)
    {
        if (json is not JsonObject obj || !IsErrorEnvelope(obj))
        {
            return null;
        }

        return new ToolErrorResult
        {
            ErrorCode = (obj["errorCode"] as JsonValue)?.GetValue<string>() ?? ToolError.Codes.InternalError,
            Message = (obj["message"] as JsonValue)?.GetValue<string>() ?? string.Empty,
            Hint = (obj["hint"] as JsonValue)?.GetValue<string>()
        };
    }
}