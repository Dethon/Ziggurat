using WebChat.Client.Models;

namespace WebChat.Client.State.UserIdentity;

public sealed record UserIdentityState
{
    // Null until the accounts service has answered, and for good on a page it could not reach.
    public SignedInPerson? SignedIn { get; init; }

    public static UserIdentityState Initial => new();
}