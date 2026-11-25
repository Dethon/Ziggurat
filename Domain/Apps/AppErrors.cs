using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Tools;

namespace Domain.Apps;

// A built app's runtime errors, spelled once for the client library that reports them and the apps
// server that keeps them: an uncaught error, an unhandled rejection, or a read the household refused or
// never answered, POSTed to OriginPath on the app's own origin.
public static class AppErrors
{
    public const string OriginPath = "/_errors";

    // What the operator's errors call a household's app that broke in somebody's browser.
    public const string ErrorType = "AppRuntimeError";

    public static class Kinds
    {
        public const string Error = "error";
        public const string Rejection = "rejection";

        // A read the household refused or never answered: it carries the read's path and envelope.
        public const string Read = "read";

        public static readonly IReadOnlyList<string> All = [Error, Rejection, Read];
    }
}

// What a page says went wrong, and nothing else: who reported it, when, from which browser and on which
// origin are the server's word (AppErrorEntry).
public sealed record AppErrorReport
{
    public required string Kind { get; init; }
    public required string Message { get; init; }
    public string? Stack { get; init; }

    // The page's path when it went wrong.
    public required string Route { get; init; }

    public AppErrorRead? Read { get; init; }

    public const int MaxMessage = 1000;
    public const int MaxStack = 8000;
    public const int MaxRoute = 1000;

    // Strict: a field the shape does not name refuses the report whole, so nothing a page sends beside
    // what went wrong — a row it read, a viewer it claims — is ever kept.
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true
    };

    public static AppErrorReportParse Parse(ReadOnlySpan<byte> body)
    {
        AppErrorReport? report;
        try
        {
            report = JsonSerializer.Deserialize<AppErrorReport>(body, _json);
        }
        catch (JsonException ex)
        {
            return new AppErrorReportParse.Refused(ex.Message);
        }

        if (report is null)
        {
            return new AppErrorReportParse.Refused("A report is one JSON object.");
        }

        var problems = report.Problems().ToList();
        return problems.Count == 0
            ? new AppErrorReportParse.Valid(report)
            : new AppErrorReportParse.Refused(string.Join(" ", problems));
    }

    private IEnumerable<string> Problems()
    {
        if (!AppErrors.Kinds.All.Contains(Kind))
        {
            yield return $"kind is one of {string.Join(", ", AppErrors.Kinds.All)}.";
        }

        if (Message.Length is 0 or > MaxMessage)
        {
            yield return $"message is 1 to {MaxMessage} characters.";
        }

        if (Stack?.Length > MaxStack)
        {
            yield return $"stack is at most {MaxStack} characters.";
        }

        // The path alone: a query or fragment is where a page keeps its state, data included.
        if (Route.Length is 0 or > MaxRoute || Route[0] != '/' || Route.IndexOfAny(['?', '#']) >= 0)
        {
            yield return $"route is the page's path, '/' and at most {MaxRoute} characters, no query or fragment.";
        }

        if ((Kind == AppErrors.Kinds.Read) != (Read is not null))
        {
            yield return "read is given for a read's error and for no other.";
        }

        if (Read is not null)
        {
            foreach (var problem in Read.Problems())
            {
                yield return problem;
            }
        }
    }
}

// A failed or refused read: which operation, on what virtual path, and the tools' envelope it was
// refused with. Never what was read.
public sealed record AppErrorRead
{
    public required string Operation { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }
    public required AppErrorEnvelope Envelope { get; init; }

    internal IEnumerable<string> Problems()
    {
        if (!AppReads.Operations.All.Contains(Operation))
        {
            yield return $"read.operation is one of {string.Join(", ", AppReads.Operations.All)}.";
        }

        if (Path is { Length: 0 or > AppErrorReport.MaxRoute } || Path is { } path && path[0] != '/')
        {
            yield return $"read.path is a virtual path, '/' and at most {AppErrorReport.MaxRoute} characters.";
        }

        if (!ToolError.IsKnown(Envelope.ErrorCode))
        {
            yield return "read.envelope.errorCode is one of the tools' codes.";
        }

        if (Envelope.Message.Length > AppErrorReport.MaxMessage || Envelope.Hint?.Length > AppErrorReport.MaxMessage)
        {
            yield return $"read.envelope's message and hint are at most {AppErrorReport.MaxMessage} characters.";
        }
    }
}

// The tools' envelope the read was refused with. Its retryability is this side's taxonomy's, never the
// page's word (a page may send it; it is not read).
public sealed record AppErrorEnvelope
{
    public required string ErrorCode { get; init; }
    public required string Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }

    public bool Retryable => ToolError.IsRetryable(ErrorCode);
}

public abstract record AppErrorReportParse
{
    public sealed record Valid(AppErrorReport Report) : AppErrorReportParse;

    public sealed record Refused(string Problem) : AppErrorReportParse;
}

// One kept runtime error, as the agent reads it at /apps/<slug>/errors.
public sealed record AppErrorEntry
{
    public required DateTimeOffset Time { get; init; }
    public required string Kind { get; init; }
    public required string Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stack { get; init; }

    public required string Route { get; init; }
    public required string UserAgent { get; init; }

    // The viewer's person id (their account's user id; the household is the app's).
    public required string Viewer { get; init; }

    // Published or Preview: which of the app's origins the page was on.
    public required string Origin { get; init; }

    public const string Published = "published";
    public const string Preview = "preview";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AppErrorRead? Read { get; init; }
}