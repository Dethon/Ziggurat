namespace Domain.DTOs.WebChat;

// SenderId says whose message it is (the account, for a person); SenderName is the name it was sent
// under, which is how somebody else reading a shared topic is shown who that was.
public record UserMessageInfo(string? SenderId, DateTimeOffset? Timestamp, string? SenderName = null);