namespace WebChat.Client.Components.Chat;

// What each action is called and drawn as in a bubble's menu, and which ones are asked about first.
public static class MessageActionLabels
{
    public static string Label(MessageActionKind action) => action switch
    {
        MessageActionKind.Delete => "Delete from here",
        _ => action.ToString()
    };

    public static IconGlyph Glyph(MessageActionKind action) => action switch
    {
        MessageActionKind.Delete => Icons.TrashStroked,
        _ => Icons.More
    };

    public static bool AsksFirst(MessageActionKind action) => action is MessageActionKind.Delete;

    public static string Question(MessageActionKind action) => action switch
    {
        MessageActionKind.Delete => "Delete this message and everything after it?",
        _ => ""
    };
}