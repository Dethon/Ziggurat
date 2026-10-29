using Domain.DTOs.WebChat;
using Domain.Security;

namespace WebChat.Client.Models;

// Who the accounts service says is signed in, as the client shows them. The id is the account's,
// the same one the hub stamps on this person's messages.
public record SignedInPerson(
    string UserId,
    string DisplayName,
    string? AvatarUrl,
    string? TenantId = null,
    IReadOnlyList<string>? Roles = null)
{
    // Which of the household's topics this person may know exist, by the rule the hub keeps; null
    // until the service has named their household.
    public TopicViewer? Viewer => TenantId is null
        ? null
        : new TopicViewer(UserId, TenantId, Roles?.Contains(ForwardedAccount.SeesAllRole) == true);
}