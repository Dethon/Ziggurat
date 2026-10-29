using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// Who a turn acts for, as the channel that vouched for them: the user id is only as true as the
// channel that issued it. A person's own message is vouched for by the channel it arrived on; a
// turn nobody sent — a schedule's fire, a login wake — carries the principal of whoever's own turn
// set it going, stamped by the server that recorded it and never written by the model.
[PublicAPI]
public record Principal(string ChannelId, string UserId);