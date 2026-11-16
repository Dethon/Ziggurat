using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Topics;

// A filter as the picker's value and as this device remembers it: "mine", "everyone" or "of:<user id>".
// Anything else — nothing remembered, a value from elsewhere — reads as the default, the person's own.
public static class TopicFilterChoice
{
    public static TopicFilter Parse(string? value) => value switch
    {
        "everyone" => TopicFilter.Everyone,
        ['o', 'f', ':', .. var owner] when owner.Length > 0 => TopicFilter.Of(owner),
        _ => TopicFilter.Mine
    };

    public static string Format(TopicFilter filter) => filter switch
    {
        { MineOnly: true } => "mine",
        { OwnerId: { } owner } => $"of:{owner}",
        _ => "everyone"
    };

    public static string Of(string userId) => Format(TopicFilter.Of(userId));
}