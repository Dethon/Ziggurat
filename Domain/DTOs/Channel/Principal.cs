using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// Who a turn acts for, as the channel that vouched for them: the user id is only as true as the
// channel that issued it. A person's own message is vouched for by the channel it arrived on, and the
// agent takes its account from the turn's assertion; a turn nobody sent — a schedule's fire, a login
// wake, a watch — acts for the principal the agent builds from the delegation it verified (the
// person's channel, user id and account), or for none: whatever its server stamped is a label.
//
// UserId is the channel's own name for the person (a Telegram username, a voice profile, the chat
// client's account id), a label beside the account and never what anything is decided by.
// Account is the person's account in the accounts service, where the channel knows it: everything
// keyed by a person moves onto it channel by channel, and it is absent wherever a channel has not
// moved yet, so a record written before it existed reads exactly as it did.
[PublicAPI]
public record Principal(string ChannelId, string UserId, Account? Account = null)
{
    // The stamped principal, and nothing else: a context's own UserId is whatever the call that
    // carried it wrote, so a context with no principal names nobody. On a server the principal is
    // the call token's (CallTurn), so this is who the agent signed the call for.
    public static Principal? Of(ConversationContext? context) => context?.Principal;
}

// A person's account: the household (tenant) they belong to and their user id in it, both opaque
// ids the accounts service issued. Never a name, so it can key what belongs to the person.
[PublicAPI]
public record Account(string TenantId, string UserId);
// An account as a channel server learns it from the accounts service: the account, the name the
// person goes by, which is what a turn's sender shows and never what anything is keyed by, and the
// service's signed assertion of that person on the channel asked about, which the channel carries on
// the person's turn untouched (it lives ten minutes, so it is carried, not kept).
[PublicAPI]
public record ResolvedAccount(Account Account, string DisplayName, string Assertion);

// The household's guest as a satellite's channel learns it: whose house it is, and the accounts
// service's signed assertion of the guest — somebody the house works for, as no person.
[PublicAPI]
public record ResolvedGuest(string TenantId, string Assertion);