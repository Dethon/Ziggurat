using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// Who a turn acts for, as the channel that vouched for them: the user id is only as true as the
// channel that issued it. A person's own message is vouched for by the channel it arrived on; a
// turn nobody sent — a schedule's fire, a login wake — carries the principal of whoever's own turn
// set it going, stamped by the server that recorded it and never written by the model.
//
// UserId is the channel's own name for the person (a Telegram username, a voice profile, the chat
// client's account id), which the identity map and the Telegram approval router still match on.
// Account is the person's account in the accounts service, where the channel knows it: everything
// keyed by a person moves onto it channel by channel, and it is absent wherever a channel has not
// moved yet, so a record written before it existed reads exactly as it did.
[PublicAPI]
public record Principal(string ChannelId, string UserId, Account? Account = null)
{
    // The stamped principal where there is one. A person's own turn without one is vouched for by
    // the channel it arrived on; a turn nobody sent has none to fall back to, because its userId is
    // what a schedule's JSON said and its origin is where it delivers.
    public static Principal? Of(ConversationContext? context) =>
        context is null
            ? null
            : context.Principal
              ?? (context.OriginKind is null ? new Principal(context.Origin.ChannelId, context.UserId) : null);
}

// A person's account: the household (tenant) they belong to and their user id in it, both opaque
// ids the accounts service issued. Never a name, so it can key what belongs to the person.
[PublicAPI]
public record Account(string TenantId, string UserId);
// An account as a channel server learns it from the accounts service: the account, and the name the
// person goes by, which is what a turn's sender shows and never what anything is keyed by.
[PublicAPI]
public record ResolvedAccount(Account Account, string DisplayName);