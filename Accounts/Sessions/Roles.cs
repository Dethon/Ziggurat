using Accounts.Store;

namespace Accounts.Sessions;

// What the session check names in X-Ziggurat-Roles: the household role, and "operator" beside it
// for the one who runs the deployment.
public static class Roles
{
    public const string Owner = "owner";
    public const string Member = "member";
    public const string Operator = "operator";

    public static IReadOnlyList<string> Of(User user) =>
        [user.Role == Role.Owner ? Owner : Member, .. user.IsOperator ? [Operator] : Array.Empty<string>()];
}