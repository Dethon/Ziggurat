using Domain.DTOs.Voice;

namespace Domain.Contracts;

// A timer that rings on its owner's channels rather than on a satellite. Only the agent holds the
// channels, so a ring is raised with it, which offers it wherever the person is and settles it
// everywhere on the first dismissal; ./dismiss stops it from here.
public interface IChannelAlerts
{
    // False when no agent was listening to take it: nothing rings, and the timer is gone like one
    // whose satellite could not be reached.
    Task<bool> RingAsync(ArmedTimer timer, CancellationToken ct);

    // Every alert of the household still ringing on its people's channels, stopped everywhere.
    Task<IReadOnlyList<DismissedAlert>> StopAllAsync(string tenantId, CancellationToken ct);
}