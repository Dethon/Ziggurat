namespace WebChat.Client.State.Admin;

public enum AdminNoticeKind
{
    Saved,
    Warning,
    Refused,
    Unavailable
}

// What an admin page tells the person after it asked the admin API: saved, saved with a caveat,
// refused with the rules' problems, or not answered. Every admin page's store keeps one, and
// AdminNoticeView draws it the same way on each.
public sealed record AdminNotice(AdminNoticeKind Kind, string Message, IReadOnlyList<string>? Problems = null)
{
    public const string UnreachableMessage = "The household could not be reached just now. Try again in a moment.";
}