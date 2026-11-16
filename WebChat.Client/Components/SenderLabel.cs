using WebChat.Client.Models;

namespace WebChat.Client.Components;

// Who a person's message is drawn as, from what the page already knows by name: the signed-in person,
// the topic's owner (the name stamped when it began), and anybody else by the name the message was
// sent under, which the transcript and the stream carry. Only a message carrying none is its id.
public static class SenderLabel
{
    public static (string? AvatarUrl, string DisplayName) Of(
        string? senderId, SignedInPerson? me, string? ownerId, string? ownerName, string? senderName = null) =>
        senderId switch
        {
            null or "" => (null, "Anonymous"),
            _ when me is not null && me.UserId == senderId => (me.AvatarUrl, me.DisplayName),
            _ when senderId == ownerId && !string.IsNullOrEmpty(ownerName) => (null, ownerName),
            _ when !string.IsNullOrEmpty(senderName) => (null, senderName),
            _ => (null, senderId)
        };
}