using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Domain.Extensions;
using Microsoft.Extensions.AI;

namespace Domain.Conversations;

// How a page names one message of a conversation, and how that name is found again. A message kept
// before entry ids has none, so it is named by its place and a hash of what it is: stable across
// reads, and refused once the message at that place is another — a name that found the wrong message
// would rewrite the wrong part of somebody's conversation.
public static class HistoryEntries
{
    private const string LegacyPrefix = "legacy:";

    // A name derived from a message's place, which is never a message's own and never kept as one.
    public static bool IsLegacy(string entryId) => entryId.StartsWith(LegacyPrefix, StringComparison.Ordinal);

    public static string IdAt(IReadOnlyList<ChatMessage> messages, int index) =>
        messages[index].GetEntryId() ?? $"{LegacyPrefix}{index}:{Fingerprint(messages[index])}";

    public static int? IndexOf(IReadOnlyList<ChatMessage> messages, string entryId)
    {
        if (!entryId.StartsWith(LegacyPrefix, StringComparison.Ordinal))
        {
            var found = messages.Index().FirstOrDefault(x => x.Item.GetEntryId() == entryId);
            return found.Item is null ? null : found.Index;
        }

        var parts = entryId[LegacyPrefix.Length..].Split(':', 2);
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
               && index < messages.Count
               && messages[index].GetEntryId() is null
               && Fingerprint(messages[index]) == parts[1]
            ? index
            : null;
    }

    private static string Fingerprint(ChatMessage message)
    {
        var said = $"{message.Role.Value}\n{message.GetTimestamp()?.ToUnixTimeMilliseconds()}\n{message.Text}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(said)))[..16];
    }
}