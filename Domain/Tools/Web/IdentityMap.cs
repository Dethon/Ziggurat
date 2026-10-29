using System.Text.Json.Nodes;
using Domain.DTOs.Channel;

namespace Domain.Tools.Web;

// One person of the deployment and the user ids that are them on each channel: a Telegram
// username, a WebChat name, an enrolled voice speaker. Per-deployment values, set in the browse
// server's compose environment and never in appsettings.json; a slot left empty is no person.
public record PersonIdentity
{
    public string Name { get; init; } = "";
    public string? Telegram { get; init; }
    public string? WebChat { get; init; }
    public string? Voice { get; init; }

    // The id this person has on one channel, by the agent's channel endpoint id. An id means
    // something only on the channel that issued it, so it is never looked for anywhere else.
    internal string? IdOn(string channelId)
    {
        var id = channelId switch
        {
            IdentityMap.TelegramChannelId => Telegram,
            IdentityMap.WebChatChannelId => WebChat,
            IdentityMap.VoiceChannelId => Voice,
            _ => null
        };
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }
}

public abstract record PersonResolution
{
    public sealed record Found(string Person) : PersonResolution;

    public sealed record Refused(JsonObject Error) : PersonResolution;
}

// Who a turn is, for browsing as a person: the call's user id resolved through the map, or a
// refusal that names why there is nobody. Nothing falls back — to the anonymous context or to
// another person — because a guess here is one person acting as another.
//
// A user id is only as true as the channel that sent it, so a turn resolves against its own
// channel's ids, and only on a channel listed as verifying who is speaking
// (Identity:VerifiedChannels). Telegram's sender is Telegram's to vouch for and voice's is an
// enrolled speaker; WebChat's is whatever its page's picker sent, so it stays off the list until a
// real login stands behind it.
public class IdentityMap(IReadOnlyList<PersonIdentity> people, IReadOnlyCollection<string> verifiedChannels)
{
    // The agent's channel endpoint ids, as a turn's origin names them.
    public const string TelegramChannelId = "telegram";
    public const string WebChatChannelId = "signalr";
    public const string VoiceChannelId = "voice";

    // The voice channel's sender when no enrolled speaker matched: somebody in the house.
    public const string HouseholdFallback = "household";

    private readonly IReadOnlyList<PersonIdentity> _people =
        people.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();

    private readonly HashSet<string> _verified = new(verifiedChannels, StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled => _people.Count > 0;

    public bool IsVerified(string channelId) => _verified.Contains(channelId);

    public PersonResolution Resolve(ConversationContext? context)
    {
        if (!IsEnabled)
        {
            return new PersonResolution.Refused(ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "Browsing as a person is not set up on this deployment: it has no identity map.",
                "Browse without asMe; pages behind a person's login cannot be reached here."));
        }

        if (context is null)
        {
            return new PersonResolution.Refused(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot tell who is asking."));
        }

        if (string.Equals(context.UserId, HouseholdFallback, StringComparison.OrdinalIgnoreCase))
        {
            return new PersonResolution.Refused(ToolError.Create(
                ToolError.Codes.PermissionDenied,
                "The speaker's voice was not recognised, so there is no person to browse as.",
                $"Tell the person their voice was not recognised; they can ask again, or ask {WhereToAsk()}."));
        }

        if (Principal.Of(context) is not { } principal)
        {
            return new PersonResolution.Refused(ToolError.Create(
                ToolError.Codes.PermissionDenied,
                "Nobody sent this turn and nobody vouched for whom it runs as, so there is no person to browse as.",
                "Browse without asMe, and say in your report that this needs the person to ask you directly."));
        }

        var channelId = principal.ChannelId;
        if (!IsVerified(channelId))
        {
            return new PersonResolution.Refused(ToolError.Create(
                ToolError.Codes.PermissionDenied,
                $"The '{channelId}' channel does not verify who is speaking yet, so nobody can be browsed as from here.",
                $"Browse without asMe, and tell the person to ask {WhereToAsk()} to reach their own accounts."));
        }

        return _people.FirstOrDefault(p =>
                string.Equals(p.IdOn(channelId), principal.UserId, StringComparison.OrdinalIgnoreCase))
            is { } person
            ? new PersonResolution.Found(person.Name)
            : new PersonResolution.Refused(ToolError.Create(
                ToolError.Codes.PermissionDenied,
                $"'{principal.UserId}' is not a person in this deployment's identity map on '{channelId}', so there is no session to browse as.",
                "Browse without asMe, and tell the person their accounts cannot be reached until the operator adds them."));
    }

    public string? WebChatNameOf(string person) =>
        _people.FirstOrDefault(p => p.Name == person)?.IdOn(WebChatChannelId);

    // The verified channels as a person would name them, for a refusal's hint: "from Telegram or
    // by voice".
    public string WhereToAsk()
    {
        var places = _verified.Order(StringComparer.Ordinal).Select(channel => channel switch
        {
            TelegramChannelId => "from Telegram",
            WebChatChannelId => "from WebChat",
            VoiceChannelId => "by voice",
            _ => $"from {channel}"
        }).ToList();
        return places.Count switch
        {
            0 => "the operator to verify a channel",
            1 => places[0],
            _ => $"{string.Join(", ", places[..^1])} or {places[^1]}"
        };
    }
}