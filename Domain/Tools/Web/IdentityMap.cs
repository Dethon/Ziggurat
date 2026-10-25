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

    internal IEnumerable<string> UserIds =>
        new[] { Telegram, WebChat, Voice }.Where(id => !string.IsNullOrWhiteSpace(id))!;
}

public abstract record PersonResolution
{
    public sealed record Found(string Person) : PersonResolution;

    public sealed record Refused(JsonObject Error) : PersonResolution;
}

// Who a turn is, for browsing as a person: the call's user id resolved through the map, or a
// refusal that names why there is nobody. Nothing falls back — to the anonymous context or to
// another person — because a guess here is one person acting as another.
public class IdentityMap(IReadOnlyList<PersonIdentity> people)
{
    // The voice channel's sender when no enrolled speaker matched: somebody in the house.
    public const string HouseholdFallback = "household";

    private readonly IReadOnlyList<PersonIdentity> _people =
        people.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();

    public bool IsEnabled => _people.Count > 0;

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
                "Tell the person their voice was not recognised; they can ask again, or ask from Telegram or WebChat."));
        }

        return _people.FirstOrDefault(p => p.UserIds.Contains(context.UserId, StringComparer.OrdinalIgnoreCase))
            is { } person
            ? new PersonResolution.Found(person.Name)
            : new PersonResolution.Refused(ToolError.Create(
                ToolError.Codes.PermissionDenied,
                $"'{context.UserId}' is not a person in this deployment's identity map, so there is no session to browse as.",
                "Browse without asMe, and tell the person their accounts cannot be reached until the operator adds them."));
    }

    public string? WebChatNameOf(string person) =>
        _people.FirstOrDefault(p => p.Name == person)?.WebChat is { Length: > 0 } name ? name : null;
}