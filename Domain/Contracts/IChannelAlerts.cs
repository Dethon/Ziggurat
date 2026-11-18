using Domain.DTOs.Channel;
using Domain.DTOs.Voice;

namespace Domain.Contracts;

// An alert that rings on its person's channels rather than on a satellite — a timer's, a glucose
// alarm's. Only the agent holds the channels, so a ring is raised with it, which offers it wherever
// the person is and settles it everywhere on the first dismissal; the raising server stops its own.
public interface IChannelAlerts
{
    // False when no agent was listening to take it: nothing rings, and the alert is gone like one
    // whose satellite could not be reached.
    Task<bool> RingAsync(ChannelAlertRequest alert, CancellationToken ct);

    // Every alert of the household this server raised and still ringing on its people's channels,
    // stopped everywhere.
    Task<IReadOnlyList<DismissedAlert>> StopAllAsync(string tenantId, CancellationToken ct);
}