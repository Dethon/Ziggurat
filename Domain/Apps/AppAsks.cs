using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Domain.Monitor;
using Domain.Tools;

namespace Domain.Apps;

// The app ask's wire, spelled once for the client library a built app's page calls (OriginPath on the
// app's own origin) and the apps server that answers it there, sending the ask to the app's building
// agent as a turn on the apps channel and waiting for the whole reply.
//
// Asked with an AppAskRequest; answered 200 with an AppAskAnswer, or the tools' envelope: what the agent
// refused or failed, and a reply to a schema that is no JSON (InvalidReply), is a 422 (as a read's refusal
// is), a conversation that cannot be continued a 410 (ConversationExpired), nobody answering a 503, the wait
// running out a 504.
public static class AppAsks
{
    public const string OriginPath = "/_ask";

    // The envelope's field carrying a reply that did not parse, on the preview alone (InvalidReply).
    public const string ReplyField = "reply";

    // Enough of an unparsed reply for its author to see what the model did instead.
    public const int MaxReplyShown = 4_000;

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

    // Any conversation this app and viewer may not continue, alike whatever the reason, so the answer says
    // nothing of whose another conversation is or whether it was ever opened.
    public static readonly ToolErrorResult ConversationExpired = ToolError.Result(ToolError.Codes.ConversationExpired,
        "This conversation cannot be continued: it is over, or it was never this app's and this person's, so nothing was asked.",
        "Ask again without the conversation, starting a new one, and say so if the person was answering a question.");

    public static readonly ToolErrorResult Unanswered = ToolError.TransientDependency(
        "The assistant ended its turn with no answer.", "Ask again.");

    public static readonly ToolErrorResult NotListening = ToolError.TransientDependency(
        "The assistant is not listening just now, so nothing was asked.", "Ask again in a moment.");

    public static ToolErrorResult TimedOut(TimeSpan waited) => ToolError.Result(ToolError.Codes.Timeout,
        $"The assistant did not answer within {waited.TotalSeconds:0} seconds, so its turn was stopped.",
        "Ask again, or ask for less at once.");

    // What an ask with a schema is answered with: its reply's JSON, whole. Nothing checks it against the
    // schema (the provider was held to it, ADR 0044); prose around it, or a fence, is not what was asked for.
    public static AppAskReplyRead ReadReply(string reply)
    {
        try
        {
            using var document = JsonDocument.Parse(reply);
            return new AppAskReplyRead.Parsed(document.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            return new AppAskReplyRead.Unparsed(ex.Message);
        }
    }

    // A reply to a schema that is no JSON. Its words are what the model wrote for this viewer, so they are
    // handed only where the author alone asks (the preview); elsewhere the page learns only that it failed.
    public static JsonObject InvalidReply(string? reply)
    {
        var envelope = ToolError.Result(ToolError.Codes.InvalidReply,
            "The assistant's answer was not JSON in the shape this app asked for.",
            "Show that the answer could not be read. If it keeps happening, the app's instructions or schema need fixing: "
            + "its author sees the answer on the preview.").ToNode();
        if (reply is not null)
        {
            envelope[ReplyField] = reply.Length > MaxReplyShown ? reply[..MaxReplyShown] : reply;
        }

        return envelope;
    }

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

public abstract record AppAskReplyRead
{
    public sealed record Parsed(JsonElement Value) : AppAskReplyRead;

    public sealed record Unparsed(string Problem) : AppAskReplyRead;
}

// What a page asks: the text, the schema its answer is to come back in, if any, and the conversation it
// continues, if any.
public sealed record AppAskRequest
{
    public required string Text { get; init; }

    public AppAskSchema? Schema { get; init; }

    // The conversation an earlier answer named, to continue it; none opens a new one.
    public string? Conversation { get; init; }

    // Strict: a field the shape does not name refuses the ask whole, so nothing a page sends is quietly
    // dropped and answered as though it had not been.
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true
    };

    private const string Shape =
        "An ask is one JSON object, {\"text\": \"...\", \"schema\": {\"name\": \"...\", \"schema\": {...}}, \"conversation\": \"...\"}, its schema and conversation optional";

    public static AppAskRequestParse Parse(ReadOnlySpan<byte> body, int maxText, int maxSchemaBytes)
    {
        AppAskRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AppAskRequest>(body, _json);
        }
        catch (JsonException ex)
        {
            return new AppAskRequestParse.Refused($"{Shape}: {ex.Message}");
        }

        return request switch
        {
            null => new AppAskRequestParse.Refused($"{Shape}."),
            { Text: var text } when string.IsNullOrWhiteSpace(text) || text.Length > maxText =>
                new AppAskRequestParse.Refused($"An ask's text is 1 to {maxText} characters."),
            { Schema: { } schema } when schema.Problem(maxSchemaBytes) is { } problem =>
                new AppAskRequestParse.Refused(problem),
            _ => new AppAskRequestParse.Valid(request)
        };
    }
}

// The shape an ask's answer must come in: a name and a JSON schema, sent to the model strict and as written
// (ADR 0044), so strict mode's rules (every property required, additionalProperties false) are the page's
// to meet. Only what would refuse the request before any model reads it is judged here.
public sealed partial record AppAskSchema
{
    // What a strict response format's name may be.
    public const int MaxNameLength = 64;

    public required string Name { get; init; }

    public required JsonElement Schema { get; init; }

    internal string? Problem(int maxBytes)
    {
        if (Name.Length is 0 or > MaxNameLength || !NameShape().IsMatch(Name))
        {
            return $"An ask's schema name is 1 to {MaxNameLength} letters, digits, _ and -.";
        }

        if (Schema.ValueKind != JsonValueKind.Object)
        {
            return "An ask's schema is a JSON schema: a JSON object, such as {\"type\": \"object\", ...}.";
        }

        var bytes = System.Text.Encoding.UTF8.GetByteCount(Schema.GetRawText());
        return bytes > maxBytes
            ? $"An ask's schema is {bytes} bytes, over the {maxBytes} bytes a schema may be: ask for less at once."
            : null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NameShape();
}

public abstract record AppAskRequestParse
{
    public sealed record Valid(AppAskRequest Request) : AppAskRequestParse;

    public sealed record Refused(string Problem) : AppAskRequestParse;
}

// The agent's answer, and the conversation it was given in: an ask with a schema gets its reply's JSON as
// value, one without its text, never both.
public sealed record AppAskAnswer
{
    public required string Conversation { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Value { get; init; }
}