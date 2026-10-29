using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// Who a turn acts for, as the channel that vouched for them: the user id is only as true as the
// channel that issued it. A person's own message is vouched for by the channel it arrived on; a
// turn nobody sent — a schedule's fire, a login wake — carries the principal of whoever's own turn
// set it going, stamped by the server that recorded it and never written by the model.
[PublicAPI]
public record Principal(string ChannelId, string UserId)
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