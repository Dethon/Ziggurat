using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class AlertService(IChatLiveConnection liveConnection) : IAlertService
{
    public Task<HubResult<bool>> DismissAlertAsync(string alertId) =>
        liveConnection.InvokeAsync<bool>("DismissAlert", alertId);

    public Task<HubResult<IReadOnlyList<AlertOfferedNotification>>> GetOfferedAlertsAsync() =>
        liveConnection.InvokeAsync<IReadOnlyList<AlertOfferedNotification>>("GetOfferedAlerts");
}