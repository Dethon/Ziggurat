using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Domain.Exceptions;
using Domain.Monitor;
using Domain.Tools;

namespace Domain.Apps;

// The app ask's wire, spelled once for the client library a built app's page calls (OriginPath on the
// app's own origin) and the apps server that answers it there, sending the ask to the app's building
// agent as a turn on the apps channel and waiting for the whole reply.
//
// Asked with an AppAskRequest; answered 200 with an AppAskAnswer, or the tools' envelope: what the agent
// refused or failed, and a reply to a schema that is no JSON (InvalidReply), is a 422 (as a read's refusal
// is), a conversation that cannot be continued a 410 (ConversationExpired), an app past its daily cap a 429
// (Capped), nobody answering a 503, the wait running out a 504.
public static class AppAsks
{
    public const string OriginPath = "/_ask";

    // An ask carrying images is a multipart form: the ask's JSON, exactly as the body of one without
    // (AppAskRequest), as the one AskField field, and each image a FilesField part. An ask without images
    // stays a JSON body, so an app that sends none never changes how it asks.
    public const string AskField = "ask";

    public const string FilesField = "files";

    // The envelope's field carrying a reply that did not parse, on the preview alone (InvalidReply).
    public const string ReplyField = "reply";

    // The envelope's field carrying what the agent said of a turn it failed, on the preview alone (Failed).
    public const string FailureField = "failure";

    // The envelope's field carrying when a capped app's next ask is counted (Capped), ISO 8601; absent when
    // none ever is.
    public const string FreesAtField = "freesAt";

    // Enough of an unparsed reply for its author to see what the model did instead.
    public const int MaxReplyShown = 4_000;

    // The instructions file's bound, in UTF-8 bytes: no fewer than its characters, at four a token it is
    // within the app_instructions section's budget (4,000 tokens), so an app inside it is never reported
    // over. ./publish refuses a file over it, and an ask from the preview, which never published, too.
    public const int MaxInstructionsBytes = 16_000;

    // What the agent's answer to a refused or failed turn is to the app. A refusal names why in the
    // agent's sentence (RefusalAnswers), which the author reads; anything else it said is the exception
    // it caught, often a provider's own body, which a viewer never reads (Failed: the preview's alone).
    // The provider refusing the schema is the one failure known to recur on every ask, so it is no retry.
    public static ToolErrorResult Refusal(string agentAnswer) => ResponseFormatRefusedException.Answers(agentAnswer)
        ? SchemaRefused
        : RefusalAnswers.CodeOf(agentAnswer) switch
        {
            RefusalAnswers.Codes.Unchecked => ToolError.TransientDependency(agentAnswer.Trim(), "Ask again in a moment."),
            null => ToolError.TransientDependency(
                "The assistant could not answer this ask.", "Ask again in a moment."),
            _ => ToolError.PermissionDenied(agentAnswer.Trim(),
                "Nothing the app can do changes this: show it to the person, who may ask an owner or whoever runs this deployment.")
        };

    // A refused or failed turn, as the page reads it. A failure's own words (the exception the agent caught,
    // often a provider's body: a strict schema it would not take, a model it does not offer) ride beside it
    // where the author alone asks, the preview, so a check can say what to fix; a viewer never reads them.
    public static JsonObject Failed(string agentAnswer, bool preview)
    {
        var envelope = Refusal(agentAnswer).ToNode();
        if (preview && RefusalAnswers.CodeOf(agentAnswer) is null)
        {
            envelope[FailureField] = agentAnswer.Length > MaxReplyShown ? agentAnswer[..MaxReplyShown] : agentAnswer;
        }

        return envelope;
    }

    // The provider refused the ask's request for its schema: asked again, it fails alike and still counts, so the
    // page is told it is no retry, and the author reads the provider's words on the preview (Failed).
    public static readonly ToolErrorResult SchemaRefused = ToolError.Result(ToolError.Codes.InvalidArgument,
        "The model's provider refused this ask's schema, so nothing was answered.",
        "Asking again fails the same way: the app's author fixes the schema (strict mode: every object "
        + "additionalProperties false, every property required) or the model; on the preview the failure says what the provider objected to.");

    public static readonly ToolErrorResult NoInstructions = ToolError.PermissionDenied(
        "This app has no instructions, so it cannot ask its assistant.",
        "Name a markdown file of its repository as instructions in app.json, and publish.");

    // Any conversation this app and viewer may not continue, alike whatever the reason, so the answer says
    // nothing of whose another conversation is or whether it was ever opened.
    public static readonly ToolErrorResult ConversationExpired = ToolError.Result(ToolError.Codes.ConversationExpired,
        "This conversation cannot be continued: it is over, or it was never this app's and this person's, so nothing was asked.",
        "Ask again without the conversation, starting a new one, and say so if the person was answering a question.");

    // An app past its daily cap: the household's asks of it, everyone's, over the last 24 hours.
    public static JsonObject Capped(int limit, DateTimeOffset? freesAt)
    {
        var envelope = ToolError.Result(ToolError.Codes.QuotaExceeded,
            $"This app has reached its daily limit of {limit} asks, counted for the whole household over the last 24 hours, so nothing was asked.",
            freesAt is { } at
                ? $"Show that the app has reached its daily limit; it may ask again from {at:yyyy-MM-dd HH:mm} UTC."
                : "Show that the app may not ask its assistant.").ToNode();
        if (freesAt is { } next)
        {
            envelope[FreesAtField] = next.ToString("O");
        }

        return envelope;
    }

    // The household's caps on its apps' asks could not be read just now: never asked as though none were set,
    // which could lift a cap an owner set lower.
    public static readonly ToolErrorResult CapUnreadable = ToolError.TransientDependency(
        "This app's daily limit could not be checked just now, so nothing was asked.", "Ask again in a moment.");

    // The accounts service would not read the household's caps for this viewer.
    public static readonly ToolErrorResult CapRefused = ToolError.PermissionDenied(
        "This app's daily limit could not be checked for this person, so nothing was asked.",
        "Show that the app cannot ask just now; signing in again may help.");

    // Most often an ask whose model stopped on calls that needed a person's approval, which an ask cannot get
    // (it ends on its third round holding such a call): only its author, reading a check, can change that.
    public static readonly ToolErrorResult Unanswered = ToolError.TransientDependency(
        "The assistant ended its turn with no answer.",
        "Ask again. If it keeps happening, the assistant may be stopping on tools that need a person's approval, "
        + "which an app's ask cannot get: its author changes the instructions or the servers the app asks with.");

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

    private static readonly string _textField = JsonNamingPolicy.CamelCase.ConvertName(nameof(Text));
    private static readonly string _schemaField = JsonNamingPolicy.CamelCase.ConvertName(nameof(Schema));
    private static readonly string _conversationField = JsonNamingPolicy.CamelCase.ConvertName(nameof(Conversation));

    private const string Shape =
        "An ask is one JSON object, {\"text\": \"...\", \"schema\": {\"name\": \"...\", \"schema\": {...}}, \"conversation\": \"...\"}, its schema and conversation optional";

    // Read by hand rather than by the serializer, whose refusals named the .NET types it read into and its own
    // paths: a page is told the field and what it must be. Strict: a field the shape does not name, or one
    // named twice, refuses the ask whole, so nothing a page sends is quietly dropped. Names match as the
    // serializer matched them, regardless of case.
    public static AppAskRequestParse Parse(ReadOnlySpan<byte> body, int maxText, int maxSchemaBytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException ex)
        {
            return new AppAskRequestParse.Refused(
                $"An ask's body is not JSON (line {(ex.LineNumber ?? 0) + 1}, byte {(ex.BytePositionInLine ?? 0) + 1}). {Shape}.");
        }

        using (document)
        {
            return Read(document.RootElement, maxText, maxSchemaBytes);
        }
    }

    private static AppAskRequestParse Read(JsonElement ask, int maxText, int maxSchemaBytes)
    {
        if (ask.ValueKind != JsonValueKind.Object)
        {
            return new AppAskRequestParse.Refused($"{Shape}.");
        }

        if (AskFields.Problem(ask, "An ask", [_textField, _schemaField, _conversationField], $" {Shape}.") is { } shape)
        {
            return new AppAskRequestParse.Refused(shape);
        }

        var textRule = $"a string of 1 to {maxText} characters";
        var text = AskFields.Find(ask, _textField);
        if (text is not { ValueKind: JsonValueKind.String })
        {
            return new AppAskRequestParse.Refused(text is null or { ValueKind: JsonValueKind.Null }
                ? $"An ask's text is missing: it must be {textRule}."
                : $"An ask's text must be {textRule}.");
        }

        if (text.Value.GetString() is not { } words || string.IsNullOrWhiteSpace(words) || words.Length > maxText)
        {
            return new AppAskRequestParse.Refused($"An ask's text is 1 to {maxText} characters.");
        }

        var conversation = AskFields.Find(ask, _conversationField);
        if (conversation is { ValueKind: not (JsonValueKind.String or JsonValueKind.Null) })
        {
            return new AppAskRequestParse.Refused("An ask's conversation must be a string, an earlier answer's, or null for a new one.");
        }

        var schema = AppAskSchema.Read(AskFields.Find(ask, _schemaField));
        if (schema is AppAskSchemaRead.Refused { Problem: var refused })
        {
            return new AppAskRequestParse.Refused(refused);
        }

        var request = new AppAskRequest
        {
            Text = words,
            Schema = ((AppAskSchemaRead.Read)schema).Schema,
            Conversation = conversation is { ValueKind: JsonValueKind.String } named ? named.GetString() : null
        };
        return request.Schema?.Problem(maxSchemaBytes) is { } problem
            ? new AppAskRequestParse.Refused(problem)
            : new AppAskRequestParse.Valid(request);
    }
}

// An object's fields as an ask's wire reads them: by name regardless of case, each named once, none outside
// the shape.
internal static class AskFields
{
    public static JsonElement? Find(JsonElement json, string name) =>
        json.EnumerateObject().Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(f => (JsonElement?)f.Value).FirstOrDefault();

    public static string? Problem(JsonElement json, string what, IReadOnlyList<string> names, string shape)
    {
        var fields = json.EnumerateObject().Select(f => f.Name).ToList();
        var unknown = fields.FirstOrDefault(f => !names.Contains(f, StringComparer.OrdinalIgnoreCase));
        var twice = fields.GroupBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)?.Key;
        return (unknown, twice) switch
        {
            ({ } name, _) => $"{what} has an unknown field '{name}'.{shape}",
            (_, { } name) => $"{what} names the field '{name}' twice.{shape}",
            _ => null
        };
    }
}

// The shape an ask's answer must come in: a name and a JSON schema, sent to the model strict and as written
// (ADR 0044), so strict mode's rules (every property required, additionalProperties false) are the page's
// to meet. Only what would refuse the request before any model reads it is judged here.
public sealed partial record AppAskSchema
{
    // The providers' own bound on a response format's name (with NameShape), judged here so a name past it is
    // the page's 400 rather than a turn the provider refuses.
    public const int MaxNameLength = 64;

    public required string Name { get; init; }

    public required JsonElement Schema { get; init; }

    private static readonly string _nameField = JsonNamingPolicy.CamelCase.ConvertName(nameof(Name));
    private static readonly string _schemaField = JsonNamingPolicy.CamelCase.ConvertName(nameof(Schema));

    private const string Shape = " It is {\"name\": \"...\", \"schema\": {...}}.";

    // An ask's schema field as sent, absent or null for none; its name and schema are judged by Problem once
    // read.
    internal static AppAskSchemaRead Read(JsonElement? field)
    {
        if (field is null or { ValueKind: JsonValueKind.Null })
        {
            return new AppAskSchemaRead.Read(null);
        }

        var schema = field.Value;
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return new AppAskSchemaRead.Refused($"An ask's schema must be an object, or null for an answer in text.{Shape}");
        }

        if (AskFields.Problem(schema, "An ask's schema", [_nameField, _schemaField], Shape) is { } shape)
        {
            return new AppAskSchemaRead.Refused(shape);
        }

        if (AskFields.Find(schema, _nameField) is not { ValueKind: JsonValueKind.String } name)
        {
            return new AppAskSchemaRead.Refused(
                $"An ask's schema name must be a string of 1 to {MaxNameLength} letters, digits, _ and -.");
        }

        return AskFields.Find(schema, _schemaField) is { } json
            ? new AppAskSchemaRead.Read(new AppAskSchema { Name = name.GetString()!, Schema = json.Clone() })
            : new AppAskSchemaRead.Refused("An ask's schema is missing its schema: a JSON object, such as {\"type\": \"object\", ...}.");
    }

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

internal abstract record AppAskSchemaRead
{
    public sealed record Read(AppAskSchema? Schema) : AppAskSchemaRead;

    public sealed record Refused(string Problem) : AppAskSchemaRead;
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

// An image as the page sent it: its name, and its bytes, whatever the page said they are.
public sealed record AppAskUpload(string FileName, byte[] Bytes);

// An image as it is kept and named on the ask's message: its type is what its bytes are.
public sealed record AppAskImage(string FileName, string MediaType, byte[] Bytes);

public abstract record AppAskImagesRead
{
    public sealed record Valid(IReadOnlyList<AppAskImage> Images) : AppAskImagesRead;

    public sealed record Refused(string Problem) : AppAskImagesRead;
}

// The images an ask may carry. Each is judged by its leading bytes, never by its name or the type the page
// declared, since that type is what the model is sent and a page can write anything there.
public static class AppAskImages
{
    // The types the apps server can tell by their bytes, and how a refusal names each; a deployment takes
    // some or all of them (Asks:ImageTypes), and a type it names beyond these is never matched.
    private static readonly IReadOnlyList<(string MediaType, string Name)> _recognised =
    [
        ("image/jpeg", "JPEG"),
        ("image/png", "PNG"),
        ("image/webp", "WebP"),
        ("image/gif", "GIF")
    ];

    public static string? Sniff(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7' or (byte)'9', (byte)'a', ..] => "image/gif",
        _ => null
    };

    public static AppAskImagesRead Read(
        IReadOnlyList<AppAskUpload> uploads, int maxImages, long maxImageBytes, IReadOnlyCollection<string> types)
    {
        if (uploads.Count > maxImages)
        {
            return new AppAskImagesRead.Refused($"At most {maxImages} images may go with one ask.");
        }

        var taken = Named(types);
        var images = uploads.Select(upload => (Upload: upload, MediaType: Sniff(upload.Bytes))).ToList();
        return images switch
        {
            _ when images.FirstOrDefault(i => i.Upload.Bytes.Length == 0) is { Upload: { } empty } =>
                new AppAskImagesRead.Refused($"The image {empty.FileName} is empty."),
            _ when images.FirstOrDefault(i => i.Upload.Bytes.LongLength > maxImageBytes) is { Upload: { } large } =>
                new AppAskImagesRead.Refused(
                    $"The image {large.FileName} is {large.Bytes.LongLength} bytes; each image is at most {maxImageBytes} bytes: send it smaller."),
            _ when images.FirstOrDefault(i => i.MediaType is null || !types.Contains(i.MediaType)) is { Upload: { } other } =>
                new AppAskImagesRead.Refused(taken is null
                    ? "This deployment takes no images with an ask."
                    : $"The image {other.FileName} is not a {taken} image."),
            _ => new AppAskImagesRead.Valid([.. images.Select(i => new AppAskImage(i.Upload.FileName, i.MediaType!, i.Upload.Bytes))])
        };
    }

    // "JPEG, PNG or GIF": the types this deployment takes, in a sentence; none when it takes none.
    private static string? Named(IReadOnlyCollection<string> types)
    {
        var names = _recognised.Where(r => types.Contains(r.MediaType)).Select(r => r.Name).ToList();
        return names.Count switch
        {
            0 => null,
            1 => names[0],
            _ => $"{string.Join(", ", names[..^1])} or {names[^1]}"
        };
    }
}