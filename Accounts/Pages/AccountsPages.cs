using System.Globalization;
using System.Net;
using Accounts.Links;
using Accounts.Passkeys;
using Accounts.Sessions;

namespace Accounts.Pages;

// The service's own pages: a link's enrollment and sign-in here, a person's account and the
// household's members in their own classes. Server-rendered, with one small script for
// navigator.credentials, so they need nothing of the apps they let people into.
public static class AccountsPages
{
    public const string ScriptPath = "/passkeys.js";
    public const string ThemeScriptPath = "/theme.js";

    // The script is the only one the pages run, and nothing else is fetched: the enrollment page's
    // address carries its link's token. Forms post only back here.
    private const string Policy =
        "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'unsafe-inline'; "
        + "base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public static void Map(IEndpointRouteBuilder accounts)
    {
        accounts.MapGet(LinkStore.EnrollPath + "/{token}", async (
            string token, HttpContext http, LinkStore links, SessionStore sessions, CancellationToken ct) =>
        {
            // Most often the link worked already and whoever opened it again holds a passkey, so the way on
            // is signing in — a button, since an installed app has nothing else to leave the page by.
            if (await links.FindOpenAsync(token, ct) is not { } link)
            {
                return Page(http, PasskeyEndpoints.RefusedLink,
                    $"""
                     <main class="card">
                       {Mark(Icons.Link)}
                       <h1>{PasskeyEndpoints.RefusedLink}</h1>
                       <p>It has been used already, a newer one was made, its week is up, or whoever made it no longer
                       manages the household.</p>
                       <p>If you made a passkey with it, you are all set: sign in with that passkey. If not, ask an owner
                       of your household for a new link.</p>
                       <a class="button primary" href="{AccountsApp.PathBase}{SignInPage.Path}">Sign in</a>
                     </main>
                     """,
                    StatusCodes.Status410Gone);
            }

            // A link opened where somebody else is signed in — an owner trying the link they meant to send,
            // a link sent to the wrong person — would sign this device in as the link's person without a
            // word: Hugo opened Sara's and was Sara afterwards.
            var cookie = http.Request.Cookies[SessionCookie.Name];
            var signedIn = string.IsNullOrEmpty(cookie) ? null : await sessions.VerifyAsync(cookie, ct);
            var somebodyElse = signedIn is not null && signedIn.UserId != link.UserId
                ? $"""
                   <p class="notice warning" role="alert">This device is signed in as <strong>{Encode(signedIn.DisplayName)}</strong>.
                   Creating a passkey here signs it in as {Encode(link.DisplayName)} instead: if this link is not yours,
                   close this page and tell whoever sent it.</p>
                   """
                : "";
            return Page(http, "Add a passkey",
                $"""
                 <main class="card" data-passkey="enroll" data-token="{Encode(token)}">
                   {Mark(Icons.Key)}
                   <h1>Add a passkey</h1>
                   <p class="lead">For <strong>{Encode(link.DisplayName)}</strong>.</p>
                   {somebodyElse}
                   <p>A passkey is how you sign in here: this device keeps it, and signing in is unlocking it the way
                   you unlock the device — a fingerprint, your face or the screen lock. No password, nothing to type.</p>
                   <button type="button" class="primary" id="go">Create a passkey</button>
                   <p id="status" class="status" role="status"></p>
                 </main>
                 """);
        });

        accounts.MapGet(SignInPage.Path, (string? returnUrl, string? signedOut, HttpContext http) =>
        {
            var justSignedOut = signedOut == "1";
            return Page(http, "Sign in",
                $"""
                 <main class="card" data-passkey="sign-in" data-return-url="{Encode(returnUrl ?? "")}"{(justSignedOut ? "" : " data-autostart")}>
                   {Mark(Icons.Key)}
                   {(justSignedOut ? "<p class=\"notice\" role=\"status\">You signed out of this device.</p>" : "")}
                   <h1>Sign in</h1>
                   <p class="lead">With the passkey you made for this household. Nothing to type: unlock it the way you
                   unlock this device.</p>
                   <button type="button" class="primary" id="go">Sign in with a passkey</button>
                   <p id="status" class="status" role="status"></p>
                   <details class="help">
                     <summary>No passkey on this device?</summary>
                     <p>If your phone holds one, choose <em>another device</em> when the passkey prompt opens and follow
                     what your phone shows.</p>
                     <p>If you have none anywhere — a new phone, a passkey you revoked — ask an owner of your household
                     for a recovery link, and open it on this device.</p>
                   </details>
                 </main>
                 """);
        });

        accounts.MapGet(ScriptPath, () => Results.Text(PasskeyScript.Source, "text/javascript"));
        accounts.MapGet(ThemeScriptPath, () => Results.Text(ThemeScript, "text/javascript"));
    }

    internal static string Encode(string text) => WebUtility.HtmlEncode(text);

    // When something happened, as the admin pages and the dashboard write it: day first, a 24-hour
    // clock, on the household's own clock (HouseholdClock) — UTC put a person in Madrid an hour behind
    // their own day.
    internal static string Date(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    // After a form has changed something, the browser goes back to the page by a GET, so reloading
    // it does not post the form again.
    internal static IResult SeeOther(HttpContext http, string path)
    {
        http.Response.Headers.Location = path;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    // A page's glyph, drawn inline: the pages fetch nothing but their own script.
    internal static string Mark(string glyph) =>
        $"""<div class="mark" aria-hidden="true"><svg viewBox="0 0 24 24" width="28" height="28" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">{glyph}</svg></div>""";

    internal static string Icon(string glyph, int size = 18) =>
        $"""<svg aria-hidden="true" viewBox="0 0 24 24" width="{size}" height="{size}" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">{glyph}</svg>""";

    internal static class Icons
    {
        public const string Key =
            """<path d="M2.586 17.414A2 2 0 0 0 2 18.828V21a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h1a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h.172a2 2 0 0 0 1.414-.586l.814-.814a6.5 6.5 0 1 0-4-4z"/><circle cx="16.5" cy="7.5" r=".5" fill="currentColor"/>""";

        public const string Link =
            """<path d="M9 17H7A5 5 0 0 1 7 7h2"/><path d="M15 7h2a5 5 0 1 1 0 10h-2"/><line x1="8" x2="16" y1="12" y2="12"/>""";

        public const string Back = """<path d="m12 19-7-7 7-7"/><path d="M19 12H5"/>""";

        public const string SignOut =
            """<path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/><polyline points="16 17 21 12 16 7"/><line x1="21" x2="9" y1="12" y2="12"/>""";

        public const string Pause = """<circle cx="12" cy="12" r="10"/><line x1="10" x2="10" y1="15" y2="9"/><line x1="14" x2="14" y1="15" y2="9"/>""";
    }

    // The theme the person chose in the chat, where these pages share its host and so its storage, set
    // before the body is drawn so the way in and out does not flash the other colours. Elsewhere, and
    // with storage refused, the system's choice stands.
    private const string ThemeScript =
        """
        (() => {
          try {
            const theme = localStorage.getItem('theme');
            if (theme === 'light' || theme === 'dark') {
              document.documentElement.setAttribute('data-theme', theme);
              document.querySelectorAll('meta[name="theme-color"]').forEach(meta => {
                meta.removeAttribute('media');
                meta.content = theme === 'dark' ? '#171310' : '#f3ede1';
              });
            }
          } catch {
            // No storage: the system's colours.
          }
        })();
        """;

    // Colours are the chat's own (WebChat.Client's app.css), so leaving the app for sign-in or the
    // account page does not feel like leaving it. Padded by the safe areas: in an installed app these
    // pages are drawn under the status bar and over the gesture bar.
    private const string Style =
        """
        :root {
          color-scheme: light;
          --bg: #f3ede1; --surface: #fffaf1; --sunken: #ece4d4;
          --text: #2a2118; --muted: #7a6a52; --border: #e6dbc6;
          --accent: #e9601f; --accent-hover: #cf5214; --on-accent: #fff3e9; --accent-soft: #fbeee2;
          --error: #c0492f; --error-bg: #fbe7df; --warning-bg: #fbf0d6; --warning: #8a6418;
          --shadow: 0 10px 30px -12px rgba(60, 40, 20, .25);
        }
        @media (prefers-color-scheme: dark) {
          :root:not([data-theme="light"]) {
            color-scheme: dark;
            --bg: #171310; --surface: #211b15; --sunken: #2c241c;
            --text: #efe4d2; --muted: #b3a389; --border: #3a2f24;
            --accent: #f0712f; --accent-hover: #f3884c; --accent-soft: #2a1d13;
            --error: #f08a70; --error-bg: #3a1c14; --warning-bg: #33280f; --warning: #e4c27a;
            --shadow: 0 10px 30px -12px rgba(0, 0, 0, .6);
          }
        }
        :root[data-theme="dark"] {
          color-scheme: dark;
          --bg: #171310; --surface: #211b15; --sunken: #2c241c;
          --text: #efe4d2; --muted: #b3a389; --border: #3a2f24;
          --accent: #f0712f; --accent-hover: #f3884c; --accent-soft: #2a1d13;
          --error: #f08a70; --error-bg: #3a1c14; --warning-bg: #33280f; --warning: #e4c27a;
          --shadow: 0 10px 30px -12px rgba(0, 0, 0, .6);
        }
        * { box-sizing: border-box; }
        html { -webkit-text-size-adjust: 100%; }
        body {
          margin: 0; min-height: 100vh; min-height: 100dvh;
          display: grid; place-items: center;
          background: var(--bg); color: var(--text);
          font: 16px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
          padding: max(1.5rem, env(safe-area-inset-top)) max(1rem, env(safe-area-inset-right))
                   max(1.5rem, env(safe-area-inset-bottom)) max(1rem, env(safe-area-inset-left));
        }
        main { width: 100%; max-width: 26rem; }
        main.card {
          background: var(--surface); border: 1px solid var(--border); border-radius: 1.25rem;
          padding: 2rem 1.5rem; box-shadow: var(--shadow);
        }
        main.wide { max-width: 44rem; align-self: start; display: grid; gap: 1rem; }
        h1 { font-size: 1.6rem; line-height: 1.25; margin: 0 0 .5rem; }
        h2 { font-size: 1.1rem; margin: 0 0 .5rem; }
        p { margin: 0 0 1rem; }
        .lead { color: var(--muted); }
        a { color: var(--accent); }
        .mark {
          width: 3.5rem; height: 3.5rem; border-radius: 1rem; margin-bottom: 1.25rem;
          display: grid; place-items: center; background: var(--accent-soft); color: var(--accent);
        }
        button, .button {
          font: inherit; font-weight: 600; display: inline-flex; align-items: center; justify-content: center; gap: .5rem;
          min-height: 2.75rem; padding: .6rem 1.1rem; border-radius: .75rem; cursor: pointer;
          border: 1px solid var(--border); background: var(--sunken); color: var(--text); text-decoration: none;
          -webkit-tap-highlight-color: transparent; touch-action: manipulation;
        }
        button:hover, .button:hover { border-color: var(--accent); }
        button:focus-visible, .button:focus-visible, summary:focus-visible, input:focus-visible, a:focus-visible {
          outline: 2px solid var(--accent); outline-offset: 2px;
        }
        .primary { width: 100%; background: var(--accent); border-color: var(--accent); color: var(--on-accent); min-height: 3.25rem; font-size: 1.05rem; }
        .primary:hover { background: var(--accent-hover); border-color: var(--accent-hover); }
        button.danger { color: var(--error); }
        button.danger:hover { border-color: var(--error); }
        button:disabled { opacity: .6; cursor: default; }
        button[aria-busy="true"]::before {
          content: ""; width: 1rem; height: 1rem; border-radius: 50%;
          border: 2px solid currentColor; border-right-color: transparent; animation: spin .8s linear infinite;
        }
        @keyframes spin { to { transform: rotate(360deg); } }
        @media (prefers-reduced-motion: reduce) { button[aria-busy="true"]::before { animation-duration: 2.4s; } }
        .status { margin: .75rem 0 0; min-height: 1.5rem; color: var(--muted); }
        .status:empty { margin: 0; min-height: 0; }
        .status.error { color: var(--error); background: var(--error-bg); padding: .6rem .8rem; border-radius: .6rem; }
        .notice { padding: .7rem .9rem; border-radius: .75rem; background: var(--sunken); }
        .notice.warning { background: var(--warning-bg); color: var(--warning); }
        details.help { margin-top: 1.5rem; border-top: 1px solid var(--border); padding-top: 1rem; color: var(--muted); }
        details.help summary { cursor: pointer; font-weight: 600; color: var(--text); }
        details.help p { margin: .75rem 0 0; }
        form { margin: 0; }
        label { display: block; font-weight: 600; font-size: .9rem; margin: .75rem 0 .3rem; }
        input, select {
          font: inherit; width: 100%; padding: .6rem .75rem; border-radius: .6rem;
          border: 1px solid var(--border); background: var(--bg); color: var(--text);
        }
        code { overflow-wrap: anywhere; font-size: .9em; background: var(--sunken); padding: .1rem .35rem; border-radius: .35rem; }
        .topbar {
          display: flex; align-items: center; gap: .5rem; flex-wrap: wrap;
          position: sticky; top: 0; z-index: 1; margin: -1.5rem -1rem 0; padding: max(.75rem, env(safe-area-inset-top)) 1rem .75rem;
          background: color-mix(in srgb, var(--bg) 92%, transparent); backdrop-filter: blur(8px);
        }
        .topbar .spacer { flex: 1; }
        .topbar a { text-decoration: none; }
        .back { font-weight: 600; display: inline-flex; align-items: center; gap: .35rem; min-height: 2.75rem; padding: 0 .5rem 0 0; }
        section { background: var(--surface); border: 1px solid var(--border); border-radius: 1rem; padding: 1.25rem; }
        section > :last-child { margin-bottom: 0; }
        .who { display: flex; align-items: center; gap: 1rem; flex-wrap: wrap; justify-content: space-between; }
        .who h1 { margin: 0; }
        .who p { margin: .25rem 0 0; color: var(--muted); }
        ul.rows { list-style: none; margin: 0 0 1rem; padding: 0; }
        ul.rows > li { display: flex; gap: .75rem; align-items: center; justify-content: space-between; flex-wrap: wrap; padding: .75rem 0; border-top: 1px solid var(--border); }
        ul.rows > li:first-child { border-top: 0; padding-top: 0; }
        .row-text { min-width: 0; flex: 1 1 14rem; }
        .row-text strong { display: block; }
        .row-text small { color: var(--muted); display: block; }
        .tag { display: inline-block; font-size: .75rem; font-weight: 600; padding: .1rem .5rem; border-radius: 999px; background: var(--accent-soft); color: var(--accent); margin-left: .35rem; vertical-align: middle; }
        .muted { color: var(--muted); }
        .actions { margin: 1rem 0 0; }
        """;

    internal static IResult Page(HttpContext http, string title, string body, int status = StatusCodes.Status200OK)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.ContentSecurityPolicy = Policy;
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Results.Content(
            $$"""
              <!doctype html>
              <html lang="en">
              <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
                <meta name="theme-color" content="#f3ede1" media="(prefers-color-scheme: light)">
                <meta name="theme-color" content="#171310" media="(prefers-color-scheme: dark)">
                <title>{{Encode(title)}} · Ziggurat</title>
                <script src="{{AccountsApp.PathBase}}{{ThemeScriptPath}}"></script>
                <style>{{Style}}</style>
              </head>
              <body>
              {{body}}
              <script src="{{AccountsApp.PathBase}}{{ScriptPath}}"></script>
              </body>
              </html>
              """,
            "text/html; charset=utf-8",
            statusCode: status);
    }
}