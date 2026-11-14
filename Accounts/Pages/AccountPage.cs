using Accounts.Configuration;
using Accounts.Links;
using Accounts.Members;
using Accounts.Passkeys;
using Accounts.People;
using Accounts.Sessions;
using Accounts.Settings;
using Accounts.Store;
using Accounts.Tunnels;
using Domain.DTOs.Household;
using Domain.DTOs.Tunnel;

namespace Accounts.Pages;

// A person's own page: the name and picture they show the household; the passkeys they hold and the
// devices they are signed in on, each revocable; adding a passkey; the code that links their
// Telegram; the command that enrolls their voice; and who in the household sees every conversation,
// which everyone in it may know.
public sealed class AccountPage(
    PageGuard guard,
    SessionStore sessions,
    PasskeyStore passkeys,
    MemberStore members,
    PersonStore people,
    LinkStore links,
    HouseholdClock clock,
    HouseholdConfigStore configuration,
    TunnelCredentialStore tunnels,
    SessionEndings endings,
    AccountsSettings settings)
{
    public const string Path = "/account";
    public const string Url = AccountsApp.PathBase + Path;
    public const string SignOutPath = "/sign-out";

    private const string LastPasskey =
        "It is your only passkey, so it stays: add another before revoking this one, or you could not sign in again.";

    public static void Map(IEndpointRouteBuilder accounts)
    {
        accounts.MapGet(Path, (HttpContext http, AccountPage page, CancellationToken ct) => page.ShowAsync(http, ct));
        accounts.MapPost(Path + "/sessions/{id}/revoke",
            (string id, HttpContext http, AccountPage page, CancellationToken ct) => page.RevokeSessionAsync(http, id, ct));
        accounts.MapPost(Path + "/passkeys/{id}/revoke",
            (string id, HttpContext http, AccountPage page, CancellationToken ct) => page.RevokePasskeyAsync(http, id, ct));
        accounts.MapPost(Path + "/telegram", (HttpContext http, AccountPage page, CancellationToken ct) => page.TelegramCodeAsync(http, ct));
        accounts.MapPost(Path + "/profile", (HttpContext http, AccountPage page, CancellationToken ct) => page.SetProfileAsync(http, ct));
        accounts.MapPost(SignOutPath, (HttpContext http, AccountPage page, CancellationToken ct) => page.SignOutAsync(http, ct));
    }

    // Where the app that opened this page said to go back to, when that is one of the deployment's;
    // every form, error page and redirect carries it on, so a save does not lose the way back.
    private string? ReturnUrl(HttpContext http) => ReturnUrls.Allowed(settings, http.Request.Query["returnUrl"]);

    private string Query(HttpContext http) =>
        ReturnUrl(http) is { } back ? $"?returnUrl={Uri.EscapeDataString(back)}" : "";

    private string Here(HttpContext http) => Url + Query(http);

    // Back: to the app, or to the front of whichever host this page is on — the chat's, Sugary's, the
    // dashboard's — rather than one of them for all.
    private string Back(HttpContext http) => ReturnUrl(http) ?? "/";

    private async Task<IResult> ShowAsync(HttpContext http, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Here(http), ownersOnly: false, ct);
        return viewer is null ? refusal! : await RenderAsync(http, viewer, telegramCode: null, ct);
    }

    private async Task<IResult> RevokeSessionAsync(HttpContext http, string sessionId, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Here(http), ownersOnly: false, ct);
        if (viewer is null)
        {
            return refusal!;
        }

        if (!await sessions.RevokeAsync(viewer.UserId, sessionId, ct))
        {
            return NotFound(http);
        }

        await endings.TellAsync(viewer.TenantId, viewer.UserId, ct);
        return sessionId == viewer.SessionId ? SignedOut(http) : AccountsPages.SeeOther(http, Here(http));
    }

    // This device's own session, ended: it stays ended whatever the household's standing, so a
    // suspended household's person can still sign a shared device out.
    private async Task<IResult> SignOutAsync(HttpContext http, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Here(http), ownersOnly: false, ct, refuseSuspended: false);
        if (viewer is null)
        {
            // No session left to end is signed out already; a stale form is told so, and nothing ends.
            return refusal is Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult ? SignedOut(http) : refusal!;
        }

        await sessions.RevokeAsync(viewer.UserId, viewer.SessionId, ct);
        await endings.TellAsync(viewer.TenantId, viewer.UserId, ct);
        return SignedOut(http);
    }

    // Off to sign-in, which says they signed out and waits to be asked, and then back to the app.
    private IResult SignedOut(HttpContext http)
    {
        SessionCookie.Clear(http.Response, settings);
        return AccountsPages.SeeOther(http, SignInPage.Url(settings, ReturnUrl(http), signedOut: true));
    }

    private async Task<IResult> RevokePasskeyAsync(HttpContext http, string passkeyId, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Here(http), ownersOnly: false, ct);
        if (viewer is null)
        {
            return refusal!;
        }

        // The passkey this device signed in with takes this device's session with it: signed out, said so.
        var signedThisDeviceIn = (await sessions.ListAsync(viewer.UserId, ct))
            .Any(d => d.Id == viewer.SessionId && d.PasskeyId == passkeyId);
        var revocation = await passkeys.RevokeOwnAsync(viewer.UserId, passkeyId, ct);
        if (revocation == OwnRevocation.Revoked)
        {
            await endings.TellAsync(viewer.TenantId, viewer.UserId, ct);
        }

        return revocation switch
        {
            OwnRevocation.Revoked => signedThisDeviceIn ? SignedOut(http) : AccountsPages.SeeOther(http, Here(http)),
            OwnRevocation.Last => AccountsPages.Page(http, "Not revoked",
                $"""
                 <main class="card">
                   <h1>That passkey was not revoked</h1>
                   <p>{LastPasskey}</p>
                   <a class="button primary" href="{AccountsPages.Encode(Here(http))}">Back to your account</a>
                 </main>
                 """, StatusCodes.Status409Conflict),
            _ => NotFound(http)
        };
    }

    // Whoever asks who is signed in (/accounts/me) and the chat hub's next connection read the
    // account's own row, so what is saved here is what they show.
    private async Task<IResult> SetProfileAsync(HttpContext http, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Here(http), ownersOnly: false, ct);
        if (viewer is null)
        {
            return refusal!;
        }

        var form = await http.Request.ReadFormAsync(ct);
        var (profile, problem) = Profile.Read(form["displayName"], form["avatarUrl"]);
        if (profile is null)
        {
            return AccountsPages.Page(http, "Not saved",
                $"""
                 <main class="card">
                   <h1>That was not saved</h1>
                   <p>{AccountsPages.Encode(problem!)}</p>
                   <a class="button primary" href="{AccountsPages.Encode(Here(http))}">Back to your account</a>
                 </main>
                 """,
                StatusCodes.Status400BadRequest);
        }

        await people.SetProfileAsync(viewer.UserId, profile, ct);
        return AccountsPages.SeeOther(http, Here(http));
    }

    // Minted when asked for rather than whenever the page is shown: only its hash is kept, so it
    // can be shown once, and a page view that wrote a link each time would fill the store.
    private async Task<IResult> TelegramCodeAsync(HttpContext http, CancellationToken ct)
    {
        var (viewer, refusal) = await guard.CheckAsync(http, Here(http), ownersOnly: false, ct);
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
        var zone = await clock.ZoneAsync(viewer.TenantId, ct);
        var seesAll = household.Where(m => m.SeesAllConversations).ToList();
        var readsSugaryLogs = household.Where(m => m.ReadsSugaryLogs && !m.Disabled).ToList();
        var configured = await configuration.HouseholdAsync(viewer.TenantId, ct);
        var householdName = (await configuration.ReadAsync(viewer.TenantId, HouseholdSections.SettingsSchema, ct))?.Name
                            ?? configured?.Name ?? "the household";
        // The home's voice server is the deployment's own; any other household's is one it set up.
        var hasVoice = configured is { IsHome: true }
                       || (await tunnels.SetUpServersAsync(viewer.TenantId, ct)).Contains(HouseholdServerNames.Voice);

        var here = AccountsPages.Encode(Query(http));
        var thisDevicesPasskey = devices.FirstOrDefault(d => d.Id == viewer.SessionId)?.PasskeyId;
        var passkeyItems = string.Concat(held.Select(p =>
            $"""
             <li data-passkey-id="{p.Id}">
               <span class="row-text"><strong>Passkey added {AccountsPages.Date(p.CreatedAt, zone)}{(p.Id == thisDevicesPasskey ? " <span class=\"tag\">signed in this device</span>" : "")}</strong>
               <small>{(p.LastUsedAt is { } used ? $"Last used {AccountsPages.Date(used, zone)}" : "Not used yet")}</small>{(held.Count > 1 ? "" : $"<small>{LastPasskey}</small>")}</span>
               {(held.Count > 1 ? $"<form method=\"post\" action=\"{Url}/passkeys/{p.Id}/revoke{here}\" data-confirm=\"Revoke this passkey? Every device it signed in is signed out, and it signs nobody in again.\">{form}<button type=\"submit\" class=\"danger\">Revoke passkey</button></form>" : "")}
             </li>
             """));
        var deviceItems = string.Concat(devices.Select(d =>
            $"""
             <li data-session-id="{d.Id}">
               <span class="row-text"><strong title="{AccountsPages.Encode(d.Device ?? "")}">{AccountsPages.Encode(DeviceNames.Of(d.Device))}</strong>{(d.Id == viewer.SessionId ? " <span class=\"tag this-device\">this device</span>" : "")}
               <small>Signed in {AccountsPages.Date(d.CreatedAt, zone)}, last seen {AccountsPages.Date(d.LastSeenAt, zone)}</small></span>
               {(d.Id == viewer.SessionId ? "" : $"<form method=\"post\" action=\"{Url}/sessions/{d.Id}/revoke{here}\">{form}<button type=\"submit\">Sign out this device</button></form>")}
             </li>
             """));

        return AccountsPages.Page(http, "Your account",
            $"""
             <main class="wide">
               {Navigation(http, me)}
               <section class="who" aria-labelledby="name">
                 <div>
                   <h1 id="name">{AccountsPages.Encode(me.DisplayName)}</h1>
                   <p>{(me.Role == Role.Owner ? "An owner" : "A member")} of {AccountsPages.Encode(householdName)}{(me.IsOperator ? ", and the deployment's operator" : "")}.</p>
                 </div>
                 <form method="post" action="{AccountsApp.PathBase}{SignOutPath}{here}">{form}<button type="submit">{AccountsPages.Icon(AccountsPages.Icons.SignOut)}Sign out</button></form>
               </section>

               <section aria-labelledby="profile">
                 <h2 id="profile">Name and picture</h2>
                 <form method="post" action="{Url}/profile{here}">{form}
                   <label for="display-name">Name</label>
                   <input id="display-name" name="displayName" required maxlength="{Profile.DisplayNameLength}" value="{AccountsPages.Encode(me.DisplayName)}" autocomplete="nickname">
                   <label for="avatar-url">Picture <span class="muted">— a web address, or a path on this site</span></label>
                   <input id="avatar-url" name="avatarUrl" maxlength="{Profile.AvatarUrlLength}" value="{AccountsPages.Encode(viewer.AvatarUrl ?? "")}" placeholder="https://… or /avatars/…" inputmode="url" autocapitalize="off" autocorrect="off" spellcheck="false">
                   <p class="actions"><button type="submit">Save</button></p>
                 </form>
               </section>

               <section aria-labelledby="passkeys">
                 <h2 id="passkeys">Passkeys</h2>
                 <p class="muted">Each passkey lives on the device, or the phone account, that made it. Revoking one also signs out every device it signed in.</p>
                 {(held.Count == 0 ? "<p>None yet.</p>" : $"<ul class=\"rows\">{passkeyItems}</ul>")}
                 <div data-passkey="add" data-antiforgery="{AccountsPages.Encode(guard.RequestToken(http))}">
                   <button type="button" id="go">{AccountsPages.Icon(AccountsPages.Icons.Key)}Add a passkey</button>
                   <p id="status" class="status" role="status"></p>
                 </div>
               </section>

               <section aria-labelledby="devices">
                 <h2 id="devices">Signed-in devices</h2>
                 <ul class="rows">{deviceItems}</ul>
               </section>

               <section aria-labelledby="telegram">
                 <h2 id="telegram">Telegram</h2>
                 {Telegram(telegramCode, form, zone, here)}
               </section>

               <section aria-labelledby="voice">
                 <h2 id="voice">Voice</h2>
                 {(hasVoice
                     ? $"<p>So that the household's voice satellites know your voice, run this on one of them and follow what it asks:</p><p><code>scripts/enroll-voice.sh {me.Id}</code></p>"
                     : "<p>The household has no voice satellites: once an owner sets up its voice server, this is where you learn how to enroll your voice.</p>")}
               </section>

               <section aria-labelledby="sees-all" data-sees-all>
                 <h2 id="sees-all">Sees all conversations</h2>
                 {(seesAll.Count == 0
                     ? "<p>Nobody in the household.</p>"
                     : $"<p>These people can read every conversation in the household, yours included:</p><ul>{string.Concat(seesAll.Select(m => $"<li>{AccountsPages.Encode(m.DisplayName)}</li>"))}</ul>")}
               </section>

               <section aria-labelledby="reads-sugary-logs" data-reads-sugary-logs>
                 <h2 id="reads-sugary-logs">Reads everyone's Sugary log</h2>
                 {(readsSugaryLogs.Count == 0
                     ? "<p>Nobody in the household.</p>"
                     : $"<p>These people's agents can read everyone's Sugary log in the household, yours included:</p><ul>{string.Concat(readsSugaryLogs.Select(m => $"<li>{AccountsPages.Encode(m.DisplayName)}</li>"))}</ul>")}
               </section>
             </main>
             """);
    }

    private string Telegram(TelegramCode? code, string form, TimeZoneInfo zone, string here)
    {
        if (code is null)
        {
            return $"""
                    <p>Link your Telegram so the bot knows you.</p>
                    <form method="post" action="{Url}/telegram{here}">{form}<button type="submit">Get a Telegram link code</button></form>
                    """;
        }

        var note = $"<p>It works once, until {AccountsPages.Date(code.ExpiresAt, zone)}.</p>";
        if (settings.Telegram.DeepLink(code.Code) is not { } deepLink)
        {
            return $"""
                    <p class="notice">Send the deployment's bot <code>/start</code> followed by this code: <code data-telegram-code>{code.Code}</code></p>
                    {note}
                    """;
        }

        return $"""
                <p class="notice">On the phone with your Telegram, open <a data-telegram-link href="{deepLink}">{deepLink}</a> and press Start.</p>
                {note}
                """;
    }

    // The page's own way back: an installed app has no Back button, and this page is not the app's.
    private string Navigation(HttpContext http, Member me) =>
        $"""
         <nav class="topbar">
           <a class="back" data-back href="{AccountsPages.Encode(Back(http))}">{AccountsPages.Icon(AccountsPages.Icons.Back, 20)}Back</a>
           <span class="spacer"></span>
           {(me.Role == Role.Owner ? $"<a href=\"{MembersPage.AdminArea}\">Members</a>" : "")}
         </nav>
         """;

    private IResult NotFound(HttpContext http) =>
        AccountsPages.Page(http, "Not found",
            $"""
             <main class="card">
               <h1>That is not yours to revoke, or it is gone already</h1>
               <a class="button primary" href="{AccountsPages.Encode(Here(http))}">Back to your account</a>
             </main>
             """,
            StatusCodes.Status404NotFound);
}