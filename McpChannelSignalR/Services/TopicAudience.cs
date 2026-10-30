using Domain.Contracts;
using Domain.DTOs.WebChat;
using Domain.Security;

namespace McpChannelSignalR.Services;

// Who in a space hears a topic's news. Every connection is in its space's group and in exactly one
// more: its person's, or — for whoever sees every topic of the household — the household's sees-all
// group. A private topic's news goes to its owner's group and the sees-all group, which no
// connection is in both of, so its owner hears it once even when they see all, and nobody else in
// the space hears it at all, not even that it went; any other topic's news goes to the space.
// Groups rather than a list of connections, because SignalR keeps them across this process's
// connections and a send to a group needs nothing remembered here.
internal static class TopicAudience
{
    public static string Space(string spaceSlug) => $"space:{spaceSlug}";

    // Every connection of one person, whatever space it is in and whatever it may see: a question
    // offered to them is theirs wherever they are, and a sees-all person is in no `user:` group.
    public static string Account(string userId) => $"account:{userId}";

    private static string Person(string spaceSlug, string userId) => $"space:{spaceSlug}:user:{userId}";

    private static string SeesAll(string spaceSlug, string tenantId) => $"space:{spaceSlug}:sees-all:{tenantId}";

    public static IReadOnlyList<string> GroupsOf(ForwardedAccount account, string spaceSlug) =>
    [
        Space(spaceSlug),
        account.SeesAll ? SeesAll(spaceSlug, account.TenantId) : Person(spaceSlug, account.UserId)
    ];

    public static IReadOnlyList<string> Of(TopicMetadata? topic, string spaceSlug) =>
        topic is { IsPrivate: true, OwnerId: { } owner }
            ? [Person(spaceSlug, owner), SeesAll(spaceSlug, topic.TenantId ?? "")]
            : [Space(spaceSlug)];

    // A topic the store does not hold — gone already, or never saved — has nobody to keep it from,
    // so its news goes to the space as every topic's once did.
    public static Task SendToTopicAsync(
        this IHubNotificationSender sender, TopicMetadata? topic, string spaceSlug, string methodName,
        object notification) =>
        Task.WhenAll(Of(topic, spaceSlug).Select(group => sender.SendToGroupAsync(group, methodName, notification)));
}