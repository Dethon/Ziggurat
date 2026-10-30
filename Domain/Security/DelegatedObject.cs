using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.DTOs.Channel;

namespace Domain.Security;

// The thing a delegation stands behind: its kind, its id, and what it does, said as one canonical
// JSON text both ends compute from their own side of it — the agent from what the turn wrote, the
// server from what it stores — so its digest is the same text hashed twice. The text travels with a
// fire beside the delegation, and the agent checks the fire against it as well as the digest: a
// server presenting the text it was delegated while firing something else is caught by the second,
// and one presenting what the object does now, changed since, by the first.
public sealed record DelegatedObject(string Kind, string Id, string Description)
{
    public const string Schedule = "schedule";
    public const string Watch = "watch";
    public const string Login = "login";

    private static readonly JsonSerializerOptions _canonical = new(JsonSerializerDefaults.Web);

    public string Digest => DigestOf(Description);

    public static string DigestOf(string description) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(description)));

    // Property order is declaration order and nothing is indented, so a record describes itself in
    // exactly one text.
    public static DelegatedObject Of<T>(string kind, string id, T description) where T : class =>
        new(kind, id, JsonSerializer.Serialize(description, _canonical));

    public static T? Read<T>(string description) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(description, _canonical);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The kind of fire each kind of object raises: a delegation for one is never believed on another.
    public static string? KindOf(MessageOriginKind origin) => origin switch
    {
        MessageOriginKind.Schedule => Schedule,
        MessageOriginKind.Watch => Watch,
        MessageOriginKind.Login => Login,
        _ => null
    };
}

// What a schedule does when it fires: which agent runs which words, when, and where the answer goes —
// the delivery as written, null for the deployment's default. One-shot timing in whole seconds since
// the epoch, so the agent reading a runAt and the server storing it agree however each spelled it.
public sealed record DelegatedSchedule(string AgentId, string Prompt, string? Cron, long? RunAt, IReadOnlyList<string>? DeliverTo);

// What a watch does as far as the agent is concerned: which agent runs it, the prompts it puts in
// front of that agent as written — a fire asks one of these words exactly, and whatever the home
// rendered of it arrives as the fire's report, never as what is asked — and where their answers go, as
// the watch resolved it when written. Its triggers and its other effects are the home's, and act for
// nobody.
public sealed record DelegatedWatch(string AgentId, IReadOnlyList<string> Prompts, IReadOnlyList<string>? DeliverTo);

// What a login handoff does: wakes the agent that asked, in the conversation that asked, answering
// where that turn would have, with the handoff's own words for the site it opened — or, for a turn
// that cannot show a link, puts the link in a new conversation of the person's chat client.
public sealed record DelegatedLogin(string AgentId, string ConversationId, ReplyTarget Origin, string Site);