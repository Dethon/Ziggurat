using Domain.Contracts;
using Domain.DTOs.WebChat;
using Domain.Security;

namespace McpChannelSignalR.Services;

// Who in a space hears a topic's news. A space is its household's — two households may both have a
// "default" — so every group names the household. Every connection is in its household's space group
// and in exactly one more: its person's, or — for whoever sees every topic of the household — the
// household's sees-all group. A private topic's news goes to its owner's group and the sees-all group,
// which no connection is in both of, so its owner hears it once even when they see all, and nobody else
// in the space hears it at all, not even that it went; any other topic's news goes to its household's
// space. A topic recording no household is nobody's to hear of. Groups rather than a list of
// connections, because SignalR keeps them across this process's connections and a send to a group
// needs nothing remembered here.
internal static class TopicAudience
{
    public static string Space(string tenantId, string spaceSlug) => $"space:{tenantId}:{spaceSlug}";

    // Every connection of one person, whatever space it is in and whatever it may see: a question
    // offered to them is theirs wherever they are, and a sees-all person is in no `user:` group.
    public static string Account(string userId) => $"account:{userId}";

    // Every connection of one household, whatever space and whoever: what the household's own
    // configuration changes (its agents) is the same news on each of its pages.
    public static string Household(string tenantId) => $"household:{tenantId}";

    private static string Person(string tenantId, string spaceSlug, string userId) =>
        $"space:{tenantId}:{spaceSlug}:user:{userId}";

    private static string SeesAll(string tenantId, string spaceSlug) => $"space:{tenantId}:{spaceSlug}:sees-all";

    public static IReadOnlyList<string> GroupsOf(ForwardedAccount account, string spaceSlug) =>
    [
        Space(account.TenantId, spaceSlug),
        account.SeesAll
            ? SeesAll(account.TenantId, spaceSlug)
            : Person(account.TenantId, spaceSlug, account.UserId)
    ];

    public static IReadOnlyList<string> Of(TopicMetadata? topic, string spaceSlug) => topic switch
    {
        { TenantId: null } => [],
        { IsPrivate: true, OwnerId: { } owner, TenantId: { } household } =>
            [Person(household, spaceSlug, owner), SeesAll(household, spaceSlug)],
        { TenantId: { } household } => [Space(household, spaceSlug)],
        null => []
    };

    // A topic the store does not hold — gone already, or never saved — names no household whose space
    // could hear of it; the hub tells its own caller's household of a delete like that itself.
    public static Task SendToTopicAsync(
        this IHubNotificationSender sender, TopicMetadata? topic, string spaceSlug, string methodName,
        object notification) =>
        Task.WhenAll(Of(topic, spaceSlug).Select(group => sender.SendToGroupAsync(group, methodName, notification)));
}