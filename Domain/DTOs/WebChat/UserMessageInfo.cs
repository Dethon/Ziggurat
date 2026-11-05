namespace Domain.DTOs.WebChat;

// SenderId says whose message it is (the account, for a person); SenderName is the name it was sent
// under, which is how somebody else reading a shared topic is shown who that was. CorrelationId is
// the id the sending page gave the send, so the page that sent it — and only that one, not the same
// person's other device — knows the message on its stream is the one it already shows.
public record UserMessageInfo(
    string? SenderId, DateTimeOffset? Timestamp, string? SenderName = null, string? CorrelationId = null);