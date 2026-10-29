using Accounts.Links;
using Accounts.Members;
using Accounts.Passkeys;
using Accounts.Sessions;
using Accounts.Settings;
using Accounts.Store;

namespace Accounts.Pages;

// A person's own page: the passkeys they hold and the devices they are signed in on, each
// revocable; adding a passkey; the code that links their Telegram; the command that enrolls their
// voice; and who in the household sees every conversation, which everyone in it may know.
public sealed class AccountPage(
    PageGuard guard,
    SessionStore sessions,
    PasskeyStore passkeys,
    MemberStore members,
    LinkStore links,
    AccountsSettings settings)
{
    public const string Path = "/account";
    public const string Url = AccountsApp.PathBase + Path;

    public static void Map(IEndpointRouteBuilder accounts)
    {
        accounts.MapGet(Path, (HttpContext http, AccountPage page, CancellationToken ct) => page.ShowAsync(http, ct));
        accounts.MapPost(Path + "/sessions/{id}/revoke",
            (string id, HttpContext http, AccountPage page, CancellationToken ct) => page.RevokeSessionAsync(http, id, ct));
        accounts.MapPost(Path + "/passkeys/{id}/revoke",
            (string id, HttpContext http, AccountPage page, CancellationToken ct) => page.RevokePasskeyAsync(http, id, ct));
        accounts.MapPost(Path + "/telegram", (HttpContext http, AccountPage page, CancellationToken ct) => page.TelegramCodeAsync(http, ct));
    }

    private async Task<IResult> ShowAsync(HttpContext http, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Url, ownersOnly: false, ct);
        return viewer is null ? refusal! : await RenderAsync(http, viewer, telegramCode: null, ct);
    }

    private async Task<IResult> RevokeSessionAsync(HttpContext http, string sessionId, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Url, ownersOnly: false, ct);
        if (viewer is null)
        {
            return refusal!;
        }

        return await sessions.RevokeAsync(viewer.UserId, sessionId, ct)
            ? AccountsPages.SeeOther(http, Url)
            : NotFound(http);
    }

    private async Task<IResult> RevokePasskeyAsync(HttpContext http, string passkeyId, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Url, ownersOnly: false, ct);
        if (viewer is null)
        {
            return refusal!;
        }

        return await passkeys.RevokeAsync(viewer.UserId, passkeyId, ct)
            ? AccountsPages.SeeOther(http, Url)
            : NotFound(http);
    }

    // Minted when asked for rather than whenever the page is shown: only its hash is kept, so it
    // can be shown once, and a page view that wrote a link each time would fill the store.
    private async Task<IResult> TelegramCodeAsync(HttpContext http, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Url, ownersOnly: false, ct);
        if (viewer is null)
        {
            return refusal!;
        }

        var code = await links.MintTelegramCodeAsync(viewer.TenantId, viewer.UserId, ct);
        return await RenderAsync(http, viewer, code, ct);
    }

    private async Task<IResult> RenderAsync(HttpContext http, SessionIdentity viewer, TelegramCode? telegramCode, CancellationToken ct)
    {
        var household = await members.ListAsync(viewer.TenantId, ct);
        var me = household.Single(m => m.Id == viewer.UserId);
        var held = await passkeys.ListAsync(viewer.UserId, ct);
        var devices = await sessions.ListAsync(viewer.UserId, ct);
        var form = guard.FormField(http);
        var seesAll = household.Where(m => m.SeesAllConversations).ToList();

        var passkeyItems = string.Concat(held.Select(p =>
            $"""
             <li data-passkey-id="{p.Id}">Added {AccountsPages.Date(p.CreatedAt)}{(p.LastUsedAt is { } used ? $", last used {AccountsPages.Date(used)}" : "")}
               <form method="post" action="{Url}/passkeys/{p.Id}/revoke">{form}<button type="submit">Revoke passkey</button></form>
             </li>
             """));
        var deviceItems = string.Concat(devices.Select(d =>
            $"""
             <li data-session-id="{d.Id}">{AccountsPages.Encode(string.IsNullOrWhiteSpace(d.Device) ? "Unknown device" : d.Device)}{(d.Id == viewer.SessionId ? " <strong>(this device)</strong>" : "")}<br>
               Signed in {AccountsPages.Date(d.CreatedAt)}, last seen {AccountsPages.Date(d.LastSeenAt)}
               <form method="post" action="{Url}/sessions/{d.Id}/revoke">{form}<button type="submit">Sign out this device</button></form>
             </li>
             """));

        return AccountsPages.Page(http, "Your account",
            $"""
             <main class="wide">
               {Navigation(me)}
               <h1>{AccountsPages.Encode(me.DisplayName)}</h1>
               <p>{(me.Role == Role.Owner ? Roles.Owner : Roles.Member)} of the household{(me.IsOperator ? ", and its operator" : "")}.</p>

               <section aria-labelledby="passkeys">
                 <h2 id="passkeys">Passkeys</h2>
                 <p>Revoking a passkey also signs out every device it signed in.</p>
                 {(held.Count == 0 ? "<p>None yet.</p>" : $"<ul>{passkeyItems}</ul>")}
                 <div data-passkey="add" data-antiforgery="{AccountsPages.Encode(guard.RequestToken(http))}">
                   <button type="button" id="go">Add a passkey</button>
                   <p id="status" role="status"></p>
                 </div>
               </section>

               <section aria-labelledby="devices">
                 <h2 id="devices">Signed-in devices</h2>
                 <ul>{deviceItems}</ul>
               </section>

               <section aria-labelledby="telegram">
                 <h2 id="telegram">Telegram</h2>
                 {Telegram(telegramCode, form)}
               </section>

               <section aria-labelledby="voice">
                 <h2 id="voice">Voice</h2>
                 <p>So that nabu knows your voice, run this on a satellite and follow what it asks:</p>
                 <p><code>scripts/enroll-voice.sh {me.Id}</code></p>
               </section>

               <section aria-labelledby="sees-all" data-sees-all>
                 <h2 id="sees-all">Sees all conversations</h2>
                 {(seesAll.Count == 0
                     ? "<p>Nobody in the household.</p>"
                     : $"<p>These people can read every conversation in the household, yours included:</p><ul>{string.Concat(seesAll.Select(m => $"<li>{AccountsPages.Encode(m.DisplayName)}</li>"))}</ul>")}
               </section>
             </main>
             """);
    }

    private string Telegram(TelegramCode? code, string form)
    {
        if (code is null)
        {
            return $"""
                    <p>Link your Telegram so the bot knows you.</p>
                    <form method="post" action="{Url}/telegram">{form}<button type="submit">Get a Telegram link code</button></form>
                    """;
        }

        var note = $"<p>It works once, until {AccountsPages.Date(code.ExpiresAt)}.</p>";
        if (settings.Telegram.BotUsername.Length == 0)
        {
            return $"""
                    <p class="notice">Send the deployment's bot <code>/start</code> followed by this code: <code data-telegram-code>{code.Code}</code></p>
                    {note}
                    """;
        }

        var deepLink = $"https://t.me/{Uri.EscapeDataString(settings.Telegram.BotUsername.TrimStart('@'))}?start={code.Code}";
        return $"""
                <p class="notice">On the phone with your Telegram, open <a data-telegram-link href="{deepLink}">{deepLink}</a> and press Start.</p>
                {note}
                """;
    }

    public static string Navigation(Member me) =>
        $"""<nav><a href="{Url}">Your account</a>{(me.Role == Role.Owner ? $" · <a href=\"{MembersPage.Url}\">Members</a>" : "")}</nav>""";

    private static IResult NotFound(HttpContext http) =>
        AccountsPages.Page(http, "Not found",
            $"""
             <main>
               <h1>That is not yours to revoke, or it is gone already.</h1>
               <p><a href="{Url}">Back to your account</a></p>
             </main>
             """,
            StatusCodes.Status404NotFound);
}