using JetBrains.Annotations;

namespace Domain.DTOs.Admin;

// The household's people as its owners manage them (.claude/rules/admin.md, "Members"): each one's
// role (`owner` or `member`), whether they run the deployment, whether they see every conversation,
// whether their agent reads everyone's Sugary log, and whether an owner disabled them — a disabled
// member is kept, with everything of theirs, and is nobody to every channel until re-enabled.
[PublicAPI]
public sealed record AdminMembers(IReadOnlyList<AdminMember> Members);

[PublicAPI]
public sealed record AdminMember(
    string Id, string DisplayName, string Role, bool IsOperator, bool SeesAll, bool Disabled, bool ReadsSugaryLogs = false)
{
    public const string Owner = "owner";
    public const string Member = "member";
}

// Somebody new, created with this name and role when they redeem the invite.
[PublicAPI]
public sealed record MemberInvite(string DisplayName, string Role)
{
    // The accounts service's own limit on a display name, the one a person's profile is held to.
    public const int DisplayNameLength = 200;
}

// What an owner changes of a member: their role, whether they see every conversation, whether their
// agent reads everyone's Sugary log, or any of them. A field left out is left as it is.
[PublicAPI]
public sealed record MemberEdit(string? Role = null, bool? SeesAll = null, bool? ReadsSugaryLogs = null);

// A link shown once, in the answer that minted it: only its hash is kept. An invite is also redeemed
// in Telegram — TelegramLink when the deployment names its bot, else TelegramStart, the message to
// send the bot; a recovery link is a passkey link alone.
[PublicAPI]
public sealed record MemberLink(string Url, DateTimeOffset ExpiresAt, string? TelegramLink = null, string? TelegramStart = null);

// A code that binds a Telegram group to the household, shown once as the command to send in the group
// once the bot is in it (only its hash is kept).
[PublicAPI]
public sealed record TelegramGroupCode(string Bind, DateTimeOffset ExpiresAt);