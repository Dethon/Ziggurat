using WebChat.Client.Models;

namespace WebChat.Client.Components;

// Who a person's message is drawn as, from what the page already knows by name: the signed-in person
// and the topic's owner (the name stamped when it began). Anybody else in a shared topic is the id
// the hub stamped until the client can ask the accounts service about them too.
public static class SenderLabel
{
    public static (string? AvatarUrl, string DisplayName) Of(
        string? senderId, SignedInPerson? me, string? ownerId, string? ownerName) =>
        senderId switch
        {
            null or "" => (null, "Anonymous"),
            _ when me is not null && me.UserId == senderId => (me.AvatarUrl, me.DisplayName),
            _ when senderId == ownerId && !string.IsNullOrEmpty(ownerName) => (null, ownerName),
            _ => (null, senderId)
        };
}