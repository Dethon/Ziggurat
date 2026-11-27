using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Tools;

namespace Domain.Apps;

// A built app's runtime errors, spelled once for the client library that reports them and the apps
// server that keeps them: an uncaught error, an unhandled rejection, a read the household refused or
// never answered, or an ask that failed, POSTed to OriginPath on the app's own origin.
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

        // An ask that failed: it carries the envelope it was answered with, never what it asked or got back.
        public const string Ask = "ask";

        public static readonly IReadOnlyList<string> All = [Error, Rejection, Read, Ask];
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

    public AppErrorAsk? Ask { get; init; }

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

        var problems = report.OwnProblems()
            .Concat(report.Read?.Problems() ?? [])
            .Concat(report.Ask?.Envelope.Problems("ask.envelope") ?? [])
            .ToList();
        return problems.Count == 0
            ? new AppErrorReportParse.Valid(report)
            : new AppErrorReportParse.Refused(string.Join(" ", problems));
    }

    // What is wrong with the report itself; a read's or an ask's own are theirs.
    private IEnumerable<string> OwnProblems()
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

        if ((Kind == AppErrors.Kinds.Ask) != (Ask is not null))
        {
            yield return "ask is given for an ask's error and for no other.";
        }
    }
}

// A failed ask: the tools' envelope it was answered with, and nothing else of it — its text, schema, images
// and any reply are what the app asked and got, and stay in the page.
public sealed record AppErrorAsk
{
    public required AppErrorEnvelope Envelope { get; init; }
}

// A failed or refused read: which operation, on what virtual path, and the tools' envelope it was
// refused with. Never what was read.
public sealed record AppErrorRead
{
    public required string Operation { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }
    public required AppErrorEnvelope Envelope { get; init; }

    internal IEnumerable<string> Problems() => OwnProblems().Concat(Envelope.Problems("read.envelope"));

    private IEnumerable<string> OwnProblems()
    {
        if (!AppReads.Operations.All.Contains(Operation))
        {
            yield return $"read.operation is one of {string.Join(", ", AppReads.Operations.All)}.";
        }

        if (Path is { Length: 0 or > AppErrorReport.MaxRoute } || Path is { } path && path[0] != '/')
        {
            yield return $"read.path is a virtual path, '/' and at most {AppErrorReport.MaxRoute} characters.";
        }
    }
}

// The tools' envelope a read or an ask was refused with. Its retryability is this side's taxonomy's, never
// the page's word (a page may send it; it is not read).
public sealed record AppErrorEnvelope
{
    public required string ErrorCode { get; init; }

    // Always given by a page; never kept from another member's (AppErrorReduction).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }

    public bool Retryable => ToolError.IsRetryable(ErrorCode);

    internal IEnumerable<string> Problems(string field)
    {
        if (!ToolError.IsKnown(ErrorCode))
        {
            yield return $"{field}.errorCode is one of the tools' codes.";
        }

        if (Message is null)
        {
            yield return $"{field}.message is given.";
        }

        if (Message?.Length > AppErrorReport.MaxMessage || Hint?.Length > AppErrorReport.MaxMessage)
        {
            yield return $"{field}'s message and hint are at most {AppErrorReport.MaxMessage} characters.";
        }
    }
}

public abstract record AppErrorReportParse
{
    public sealed record Valid(AppErrorReport Report) : AppErrorReportParse;

    public sealed record Refused(string Problem) : AppErrorReportParse;
}

// One kept runtime error, as the agent reads it at /apps/<slug>/errors. The author's own report keeps the
// page's text; another member's keeps only what the server checked (AppErrorReduction) and says so.
public sealed record AppErrorEntry
{
    public required DateTimeOffset Time { get; init; }
    public required string Kind { get; init; }

    // A reduced report's error type: a standard one, else Error.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stack { get; init; }

    // A reduced report's stack: each place in the app's own files, `/path:line:column`, innermost first.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Frames { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Route { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserAgent { get; init; }

    // The viewer's person id (their account's user id; the household is the app's).
    public required string Viewer { get; init; }

    // Published or Preview: which of the app's origins the page was on.
    public required string Origin { get; init; }

    public const string Published = "published";
    public const string Preview = "preview";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AppErrorRead? Read { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AppErrorAsk? Ask { get; init; }

    // Set on another member's report, saying what of it was kept and what to do instead.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reduced { get; init; }
}