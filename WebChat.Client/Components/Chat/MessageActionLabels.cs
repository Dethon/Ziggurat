namespace WebChat.Client.Components.Chat;

// One choice made in a bubble's menu: the action, and the model a reroll was asked on.
public sealed record MessageActionChoice(MessageActionKind Kind, string? Model = null);

// What each action is called and drawn as in a bubble's menu, and which ones are asked about first.
public static class MessageActionLabels
{
    public static string Label(MessageActionKind action) => action switch
    {
        MessageActionKind.Reroll => "Reroll",
        MessageActionKind.RerollWith => "Reroll with…",
        MessageActionKind.Edit => "Edit",
        MessageActionKind.Fork => "Fork from here",
        MessageActionKind.Delete => "Delete from here",
        _ => action.ToString()
    };

    public static IconGlyph Glyph(MessageActionKind action) => action switch
    {
        MessageActionKind.Reroll or MessageActionKind.RerollWith => Icons.Retry,
        MessageActionKind.Edit => Icons.Pencil,
        MessageActionKind.Fork => Icons.Fork,
        MessageActionKind.Delete => Icons.TrashStroked,
        _ => Icons.More
    };

    // Deleting discards; running a reply again may do again what its tools did.
    public static bool AsksFirst(MessageActionKind action, IReadOnlyList<string> tools) =>
        action is MessageActionKind.Delete
        || (action is MessageActionKind.Reroll or MessageActionKind.RerollWith && tools.Count > 0);

    public static string Question(MessageActionKind action, IReadOnlyList<string> tools) => action switch
    {
        MessageActionKind.Delete => "Delete this message and everything after it?",
        _ => $"This reply used {string.Join(", ", tools)}. Running it again may do them again."
    };

    public static string Confirm(MessageActionKind action) => action switch
    {
        MessageActionKind.Delete => "Delete",
        _ => "Run it again"
    };
}