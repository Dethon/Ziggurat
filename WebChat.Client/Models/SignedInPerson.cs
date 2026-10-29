namespace WebChat.Client.Models;

// Who the accounts service says is signed in, as the client shows them. The id is the account's,
// the same one the hub stamps on this person's messages.
public record SignedInPerson(string UserId, string DisplayName, string? AvatarUrl);