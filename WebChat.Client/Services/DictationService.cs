using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class DictationService(IChatLiveConnection liveConnection) : IDictationService
{
    public Task<HubResult<DictationTicket>> CreateTicketAsync() =>
        liveConnection.InvokeAsync<DictationTicket>("CreateDictationTicket");
}