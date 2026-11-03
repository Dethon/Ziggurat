namespace Accounts.Pages;

// The household's members are managed in the chat client's admin area (/admin/members, over the admin
// API): this service's own members page was a second one, and a link from before it was retired is
// sent there.
public static class MembersPage
{
    public const string Path = "/members";

    public const string AdminArea = "/admin/members";

    public static void Map(RouteGroupBuilder accounts) =>
        accounts.MapGet(Path, () => Results.Redirect(AdminArea));
}