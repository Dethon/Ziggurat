using Accounts.Store;

namespace Accounts.Sessions;

// What the session check names in X-Ziggurat-Roles: the household role, "operator" beside it for the
// one who runs the deployment, and "sees-all" for whoever an owner granted every topic in the
// household — named here so the chat hub learns it from the headers it already reads.
public static class Roles
{
    public const string Owner = "owner";
    public const string Member = "member";
    public const string Operator = "operator";
    public const string SeesAll = "sees-all";

    public static IReadOnlyList<string> Of(User user) =>
    [
        user.Role == Role.Owner ? Owner : Member,
        .. user.IsOperator ? [Operator] : Array.Empty<string>(),
        .. user.SeesAllConversations ? [SeesAll] : Array.Empty<string>()
    ];
}