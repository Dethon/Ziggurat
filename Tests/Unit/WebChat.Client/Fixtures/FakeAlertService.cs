using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeAlertService : IAlertService
{
    public List<AlertOfferedNotification> Ringing { get; } = [];

    public List<string> Dismissed { get; } = [];

    // Set to answer not live for every call, the way a transport between connections does.
    public bool NotLive { get; set; }

    public Task<HubResult<bool>> DismissAlertAsync(string alertId)
    {
        if (NotLive)
        {
            return Task.FromResult(HubResult<bool>.NotLive);
        }

        Dismissed.Add(alertId);
        return Task.FromResult(HubResult<bool>.Answered(Ringing.RemoveAll(a => a.AlertId == alertId) > 0));
    }

    public Task<HubResult<IReadOnlyList<AlertOfferedNotification>>> GetOfferedAlertsAsync() =>
        Task.FromResult(NotLive
            ? HubResult<IReadOnlyList<AlertOfferedNotification>>.NotLive
            : HubResult<IReadOnlyList<AlertOfferedNotification>>.Answered([.. Ringing]));
}