using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Monitor;
using Domain.Tools;

namespace Domain.Apps;

// The app ask's wire, spelled once for the client library a built app's page calls (OriginPath on the
// app's own origin) and the apps server that answers it there, sending the ask to the app's building
// agent as a turn on the apps channel and waiting for the whole reply.
//
// Answered 200 with an AppAskAnswer, or the tools' envelope: what the agent refused or failed is a 422
// (as a read's refusal is), nobody answering a 503, the wait running out a 504.
public static class AppAsks
{
    public const string OriginPath = "/_ask";

    // The instructions file's bound, in UTF-8 bytes: no fewer than its characters, at four a token it is
    // within the app_instructions section's budget (4,000 tokens), so an app inside it is never reported
    // over. ./publish refuses a file over it, and an ask from the preview, which never published, too.
    public const int MaxInstructionsBytes = 16_000;

    // What the agent's answer to a refused or failed turn is to the app. A refusal names why in the
    // agent's sentence (RefusalAnswers), which the author reads; anything else it said is the exception
    // it caught, often a provider's own body, and stays in the server's log.
    public static ToolErrorResult Refusal(string agentAnswer) => RefusalAnswers.CodeOf(agentAnswer) switch
    {
        "unchecked" => ToolError.TransientDependency(agentAnswer.Trim(), "Ask again in a moment."),
        null => ToolError.TransientDependency(
            "The assistant could not answer this ask.", "Ask again in a moment."),
        _ => ToolError.PermissionDenied(agentAnswer.Trim(),
            "Nothing the app can do changes this: show it to the person, who may ask an owner or whoever runs this deployment.")
    };

    public static readonly ToolErrorResult NoInstructions = ToolError.PermissionDenied(
        "This app has no instructions, so it cannot ask its assistant.",
        "Name a markdown file of its repository as instructions in app.json, and publish.");

    public static readonly ToolErrorResult Unanswered = ToolError.TransientDependency(
        "The assistant ended its turn with no answer.", "Ask again.");

    public static readonly ToolErrorResult NotListening = ToolError.TransientDependency(
        "The assistant is not listening just now, so nothing was asked.", "Ask again in a moment.");

    public static ToolErrorResult TimedOut(TimeSpan waited) => ToolError.Result(ToolError.Codes.Timeout,
        $"The assistant did not answer within {waited.TotalSeconds:0} seconds, so its turn was stopped.",
        "Ask again, or ask for less at once.");

    // The instructions as the agent is sent them: UTF-8 text of at most the bound, or why not.
    public static AppInstructionsRead ReadInstructions(string path, byte[]? file)
    {
        if (file is null)
        {
            return new AppInstructionsRead.Refused($"the instructions {path} are not a file of the app.");
        }

        if (file.Length > MaxInstructionsBytes)
        {
            return new AppInstructionsRead.Refused(
                $"the instructions {path} are over the {MaxInstructionsBytes} bytes an app's instructions may be: say it in fewer words.");
        }

        string text;
        try
        {
            text = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(file);
        }
        catch (ArgumentException)
        {
            return new AppInstructionsRead.Refused($"the instructions {path} are not UTF-8 text.");
        }

        return string.IsNullOrWhiteSpace(text)
            ? new AppInstructionsRead.Refused($"the instructions {path} are empty.")
            : new AppInstructionsRead.Read(text);
    }
}

public abstract record AppInstructionsRead
{
    public sealed record Read(string Text) : AppInstructionsRead;

    public sealed record Refused(string Problem) : AppInstructionsRead;
}

// What a page asks: the text alone, for now.
public sealed record AppAskRequest
{
    public required string Text { get; init; }

    // Strict: a field the shape does not name refuses the ask whole, so nothing a page sends is quietly
    // dropped (a conversation to continue, a schema) and answered as though it had not been.
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true
    };

    public static AppAskRequestParse Parse(ReadOnlySpan<byte> body, int maxText)
    {
        AppAskRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AppAskRequest>(body, _json);
        }
        catch (JsonException ex)
        {
            return new AppAskRequestParse.Refused($"An ask is one JSON object, {{\"text\": \"...\"}}: {ex.Message}");
        }

        return request switch
        {
            null => new AppAskRequestParse.Refused("An ask is one JSON object, {\"text\": \"...\"}."),
            { Text: var text } when string.IsNullOrWhiteSpace(text) || text.Length > maxText =>
                new AppAskRequestParse.Refused($"An ask's text is 1 to {maxText} characters."),
            _ => new AppAskRequestParse.Valid(request)
        };
    }
}

public abstract record AppAskRequestParse
{
    public sealed record Valid(AppAskRequest Request) : AppAskRequestParse;

    public sealed record Refused(string Problem) : AppAskRequestParse;
}

// The agent's answer, and the conversation it was given in.
public sealed record AppAskAnswer
{
    public required string Conversation { get; init; }
    public required string Text { get; init; }
}