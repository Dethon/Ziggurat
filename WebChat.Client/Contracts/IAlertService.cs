using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface IAlertService
{
    Task<HubResult<bool>> DismissAlertAsync(string alertId);
    Task<HubResult<IReadOnlyList<AlertOfferedNotification>>> GetOfferedAlertsAsync();
}