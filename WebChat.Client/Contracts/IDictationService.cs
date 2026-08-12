using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

// Permission to turn a recording into words. Only the live connection can mint it, and it is
// minted per dictation rather than per message: nothing is stored, so there is no slot to count.
public interface IDictationService
{
    Task<HubResult<DictationTicket>> CreateTicketAsync();
}