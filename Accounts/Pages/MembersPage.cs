using Accounts.Links;
using Accounts.Members;
using Accounts.Sessions;
using Accounts.Settings;
using Accounts.Store;

namespace Accounts.Pages;

// The household as its owners manage it: everyone in it with their role, the operator flag and the
// permission to see every conversation; inviting somebody new; a recovery link for someone who lost
// every passkey; changing roles; granting and withdrawing the permission; a code that binds a
// Telegram group to the household. A member is refused it.
public sealed class MembersPage(PageGuard guard, MemberStore members, LinkStore links, AccountsSettings settings)
{
    public const string Path = "/members";
    public const string Url = AccountsApp.PathBase + Path;

    private const int DisplayNameLength = 200;

    public static void Map(IEndpointRouteBuilder accounts)
    {
        accounts.MapGet(Path, (HttpContext http, MembersPage page, CancellationToken ct) => page.ShowAsync(http, ct));
        accounts.MapPost(Path + "/invite", (HttpContext http, MembersPage page, CancellationToken ct) => page.InviteAsync(http, ct));
        accounts.MapPost(Path + "/{id}/recovery",
            (string id, HttpContext http, MembersPage page, CancellationToken ct) => page.RecoveryAsync(http, id, ct));
        accounts.MapPost(Path + "/{id}/role",
            (string id, HttpContext http, MembersPage page, CancellationToken ct) => page.ChangeRoleAsync(http, id, ct));
        accounts.MapPost(Path + "/{id}/sees-all",
            (string id, HttpContext http, MembersPage page, CancellationToken ct) => page.SeesAllAsync(http, id, ct));
        accounts.MapPost(Path + "/telegram-group", (HttpContext http, MembersPage page, CancellationToken ct) => page.GroupCodeAsync(http, ct));
    }

    private async Task<IResult> ShowAsync(HttpContext http, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Url, ownersOnly: true, ct);
        return viewer is null ? refusal! : await RenderAsync(http, viewer, null, StatusCodes.Status200OK, ct);
    }

    // The link is shown in this answer and nowhere else: only its hash is kept.
    private async Task<IResult> InviteAsync(HttpContext http, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Url, ownersOnly: true, ct);
        if (viewer is null)
        {
            return refusal!;
        }

        var form = await http.Request.ReadFormAsync(ct);
        var displayName = form["displayName"].ToString().Trim();
        if (displayName.Length is 0 or > DisplayNameLength || RoleOf(form["role"]) is not { } role)
        {
            return await RenderAsync(http, viewer,
                "<p class=\"notice\">An invite needs a display name of up to 200 characters and a role.</p>",
                StatusCodes.Status400BadRequest, ct);
        }

        var invite = await links.MintInviteAsync(viewer.TenantId, viewer.UserId, displayName, role, ct);
        return await RenderAsync(http, viewer,
            Issued($"An invite for {displayName}, as {Name(role)}", invite.Url, invite.ExpiresAt, InTelegram(invite.Token)),
            StatusCodes.Status200OK, ct);
    }

    // The same invite redeemed in Telegram instead of a browser, for somebody who never uses the web:
    // its token is the bot's start parameter, and the bot links the Telegram that sends it.
    private string InTelegram(string token) =>
        settings.Telegram.DeepLink(token) is { } deepLink
            ? $"""<p>Or, for someone who only uses Telegram, open it there: <a data-telegram-link href="{deepLink}">{deepLink}</a></p>"""
            : $"""<p>Or, for someone who only uses Telegram, send the deployment's bot <code data-telegram-start>/start {token}</code> from it.</p>""";

    // Shown once, like an invite: only its hash is kept.
    private async Task<IResult> GroupCodeAsync(HttpContext http, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Url, ownersOnly: true, ct);
        if (viewer is null)
        {
            return refusal!;
        }

        var code = await links.MintGroupCodeAsync(viewer.TenantId, viewer.UserId, ct);
        return await RenderAsync(http, viewer,
            $"""
             <section class="notice" aria-label="New group code">
               <p>Add the bot to the group, then send this there. It works once, until {AccountsPages.Date(code.ExpiresAt)}, and is not shown again.</p>
               <p><code data-bind-command>/bind {code.Code}</code></p>
             </section>
             """,
            StatusCodes.Status200OK, ct);
    }

    // Not for the operator: their own way back in is the container command, and an owner who could
    // mint them a link could take the deployment's dashboard.
    private async Task<IResult> RecoveryAsync(HttpContext http, string userId, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Url, ownersOnly: true, ct);
        if (viewer is null)
        {
            return refusal!;
        }

        var person = (await members.ListAsync(viewer.TenantId, ct)).FirstOrDefault(m => m.Id == userId);
        if (person is null)
        {
            return await RenderAsync(http, viewer, NobodyNotice, StatusCodes.Status404NotFound, ct);
        }

        if (person.IsOperator)
        {
            return await RenderAsync(http, viewer,
                "<p class=\"notice\">The operator recovers their account with the accounts container's command.</p>",
                StatusCodes.Status403Forbidden, ct);
        }

        var link = (await links.MintRecoveryAsync(viewer.TenantId, viewer.UserId, userId, ct))!;
        return await RenderAsync(http, viewer, Issued($"A recovery link for {person.DisplayName}", link.Url, link.ExpiresAt),
            StatusCodes.Status200OK, ct);
    }

    private async Task<IResult> ChangeRoleAsync(HttpContext http, string userId, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Url, ownersOnly: true, ct);
        if (viewer is null)
        {
            return refusal!;
        }

        if (RoleOf((await http.Request.ReadFormAsync(ct))["role"]) is not { } role)
        {
            return await RenderAsync(http, viewer, "<p class=\"notice\">That is not a role.</p>", StatusCodes.Status400BadRequest, ct);
        }

        return await members.ChangeRoleAsync(viewer.TenantId, userId, role, ct) switch
        {
            RoleChange.Changed => AccountsPages.SeeOther(http, Url),
            RoleChange.LastOwner => await RenderAsync(http, viewer,
                "<p class=\"notice\">The household's last owner cannot become a member: make someone else an owner first.</p>",
                StatusCodes.Status409Conflict, ct),
            _ => await RenderAsync(http, viewer, NobodyNotice, StatusCodes.Status404NotFound, ct)
        };
    }

    private async Task<IResult> SeesAllAsync(HttpContext http, string userId, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Url, ownersOnly: true, ct);
        if (viewer is null)
        {
            return refusal!;
        }

        if (!bool.TryParse((await http.Request.ReadFormAsync(ct))["grant"], out var granted))
        {
            return await RenderAsync(http, viewer, "<p class=\"notice\">Grant or withdraw?</p>", StatusCodes.Status400BadRequest, ct);
        }

        return await members.SetSeesAllConversationsAsync(viewer.TenantId, userId, granted, ct)
            ? AccountsPages.SeeOther(http, Url)
            : await RenderAsync(http, viewer, NobodyNotice, StatusCodes.Status404NotFound, ct);
    }

    private const string NobodyNotice = "<p class=\"notice\">There is nobody like that in the household.</p>";

    private async Task<IResult> RenderAsync(HttpContext http, SessionIdentity viewer, string? notice, int status, CancellationToken ct)
    {
        var household = await members.ListAsync(viewer.TenantId, ct);
        var me = household.Single(m => m.Id == viewer.UserId);
        var form = guard.FormField(http);
        var rows = string.Concat(household.Select(m =>
            $"""
             <tr data-member="{m.Id}">
               <th scope="row">{AccountsPages.Encode(m.DisplayName)}</th>
               <td data-role>{Name(m.Role)}</td>
               <td>{(m.IsOperator ? "operator" : "")}</td>
               <td>{(m.SeesAllConversations ? "yes" : "no")}
                 <form method="post" action="{Url}/{m.Id}/sees-all">{form}<input type="hidden" name="grant" value="{(m.SeesAllConversations ? "false" : "true")}"><button type="submit">{(m.SeesAllConversations ? "Withdraw" : "Grant")} sees all conversations</button></form>
               </td>
               <td>
                 <form method="post" action="{Url}/{m.Id}/role">{form}
                   <select name="role" aria-label="Change {AccountsPages.Encode(m.DisplayName)}'s role">{Options(m.Role)}</select>
                   <button type="submit">Change role</button>
                 </form>
                 {(m.IsOperator ? "" : $"""<form method="post" action="{Url}/{m.Id}/recovery">{form}<button type="submit">Recovery link</button></form>""")}
               </td>
             </tr>
             """));

        return AccountsPages.Page(http, "Members",
            $"""
             <main class="wide">
               {AccountPage.Navigation(me)}
               <h1>Members</h1>
               {notice}
               <table>
                 <thead><tr><th scope="col">Name</th><th scope="col">Role</th><th scope="col"></th><th scope="col">Sees all conversations</th><th scope="col"></th></tr></thead>
                 <tbody>{rows}</tbody>
               </table>

               <section aria-labelledby="invite">
                 <h2 id="invite">Invite someone</h2>
                 <p>The link works once, for a week, and creates them with the name and role you give here.</p>
                 <form class="block" method="post" action="{Url}/invite">{form}
                   <label for="invite-name">Display name</label>
                   <input id="invite-name" name="displayName" required maxlength="{DisplayNameLength}">
                   <label for="invite-role">Role</label>
                   <select id="invite-role" name="role">{Options(Role.Member)}</select>
                   <button type="submit">Create invite</button>
                 </form>
               </section>

               <section aria-labelledby="telegram-group">
                 <h2 id="telegram-group">Telegram groups</h2>
                 <p>In a group bound to the household, its linked members' messages to the bot are their own turns. The bot answers nobody in a group that is not.</p>
                 <form method="post" action="{Url}/telegram-group">{form}<button type="submit">Bind a Telegram group</button></form>
               </section>
             </main>
             """,
            status);
    }

    private static string Issued(string what, Uri url, DateTimeOffset expiresAt, string? alternative = null) =>
        $"""
         <section class="notice" aria-label="New link">
           <p>{AccountsPages.Encode(what)}, good once until {AccountsPages.Date(expiresAt)}. Send it to them; it is not shown again.</p>
           <p><a data-link href="{url}">{url}</a></p>
           {alternative}
         </section>
         """;

    private static string Options(Role selected) =>
        string.Concat(new[] { Role.Member, Role.Owner }.Select(r =>
            $"""<option value="{Name(r)}"{(r == selected ? " selected" : "")}>{Name(r)}</option>"""));

    private static string Name(Role role) => role == Role.Owner ? Roles.Owner : Roles.Member;

    private static Role? RoleOf(string? value) => value switch
    {
        Roles.Owner => Role.Owner,
        Roles.Member => Role.Member,
        _ => null
    };
}