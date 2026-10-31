using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.Contracts;

// A channel connection as an insistent alert sees it (AlertRelay): what its server raises, and
// whether it can put one to a person. A contract beside IChannelConnection rather than inside it,
// since an alert is no part of a turn — nothing a conversation group, a delivery or an approval
// asks of a channel changes for it.
//
// Not connected is three behaviours, like the five of IChannelConnection: Alerts yields nothing
// until the server is connected again, an offer is false, and a settle returns silently.
[PublicAPI]
public interface IAlertChannel
{
    string ChannelId { get; }

    // Everything this channel's server raised, for as long as the connection object lives: alerts to
    // ring, and alerts it stopped where they were raised.
    IAsyncEnumerable<AlertSignal> Alerts { get; }

    // The alert put to its person here, held until they dismiss it here (true) or it is called off.
    // False is every other way of not being dismissed here — a channel that shows no alert, one that
    // could not reach the person, a refusal, not connected — and never ends the alert.
    Task<bool> OfferAlertAsync(ChannelAlertNotification alert, CancellationToken ct);

    // The alert ended elsewhere: whatever still shows it here stops, saying whether it was dismissed
    // or rang out. A no-op for an alert this channel never showed.
    Task SettleAlertAsync(string alertId, bool dismissed, CancellationToken ct);
}