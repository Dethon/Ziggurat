using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

public enum TelegramStartOutcome
{
    // The invite created its person, linked to the Telegram that sent it.
    Invited,

    // The account page's code linked its person to the Telegram that sent it.
    Linked,

    // Nothing open goes by that code: used, past its week, or never one.
    CodeRefused,

    // The Telegram that sent it is somebody's already.
    TelegramTaken,

    // The code is a suspended household's: nothing was spent, and it opens once it is resumed.
    Suspended
}

// A start parameter redeemed: who the Telegram that sent it now is, where it worked.
[PublicAPI]
public record TelegramStart(TelegramStartOutcome Outcome, ResolvedAccount? Account = null);

public enum TelegramWebLinkOutcome
{
    Minted,

    // The Telegram that asked is nobody's.
    NotLinked,

    // The person it is must not be handed one through Telegram (the operator).
    Refused,

    // The person's household is suspended.
    Suspended
}

// A one-time link that adds a passkey to the account a Telegram is linked to.
[PublicAPI]
public record TelegramWebLink(TelegramWebLinkOutcome Outcome, Uri? Url = null, DateTimeOffset? ExpiresAt = null);

public enum TelegramBindOutcome
{
    Bound,

    // Nothing open goes by that code: used, past its week, or never one.
    CodeRefused,

    // The group belongs to another household.
    BoundElsewhere
}

[PublicAPI]
public record TelegramBind(TelegramBindOutcome Outcome, string? TenantId = null);