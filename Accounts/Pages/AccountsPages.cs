using System.Globalization;
using System.Net;
using Accounts.Links;
using Accounts.Passkeys;

namespace Accounts.Pages;

// The service's own pages: a link's enrollment and sign-in here, a person's account and the
// household's members in their own classes. Server-rendered, with one small script for
// navigator.credentials, so they need nothing of the apps they let people into.
public static class AccountsPages
{
    public const string ScriptPath = "/passkeys.js";

    // The script is the only one the pages run, and nothing else is fetched: the enrollment page's
    // address carries its link's token. Forms post only back here.
    private const string Policy =
        "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'unsafe-inline'; "
        + "base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public static void Map(IEndpointRouteBuilder accounts)
    {
        accounts.MapGet(LinkStore.EnrollPath + "/{token}", async (string token, HttpContext http, LinkStore links, CancellationToken ct) =>
        {
            if (await links.FindOpenAsync(token, ct) is not { } link)
            {
                return Page(http, PasskeyEndpoints.RefusedLink,
                    $"""
                     <main>
                       <h1>{PasskeyEndpoints.RefusedLink}</h1>
                       <p>It has been used already, or its week is up. Ask an owner of your household for a new one.</p>
                     </main>
                     """,
                    StatusCodes.Status410Gone);
            }

            return Page(http, "Add a passkey",
                $"""
                 <main data-passkey="enroll" data-token="{Encode(token)}">
                   <h1>Add a passkey</h1>
                   <p>For <strong>{Encode(link.DisplayName)}</strong>. This device keeps the passkey, and signing in is
                   choosing it, with nothing to type.</p>
                   <button type="button" id="go">Create a passkey</button>
                   <p id="status" role="status"></p>
                 </main>
                 """);
        });

        accounts.MapGet(SignInPage.Path, (string? returnUrl, HttpContext http) =>
            Page(http, "Sign in",
                $"""
                 <main data-passkey="sign-in" data-return-url="{Encode(returnUrl ?? "")}">
                   <h1>Sign in</h1>
                   <p>Choose your passkey when this device asks for it.</p>
                   <button type="button" id="go">Sign in with a passkey</button>
                   <p id="status" role="status"></p>
                 </main>
                 """));

        accounts.MapGet(ScriptPath, () => Results.Text(PasskeyScript.Source, "text/javascript"));
    }

    internal static string Encode(string text) => WebUtility.HtmlEncode(text);

    internal static string Date(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    // After a form has changed something, the browser goes back to the page by a GET, so reloading
    // it does not post the form again.
    internal static IResult SeeOther(HttpContext http, string path)
    {
        http.Response.Headers.Location = path;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

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
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{Encode(title)}} · Ziggurat</title>
                <style>
                  :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
                  body { margin: 0; min-height: 100vh; display: grid; place-items: center; }
                  main { max-width: 26rem; padding: 0 1rem; }
                  main.wide { max-width: 44rem; padding: 2rem 1rem; }
                  button { font: inherit; padding: .7rem 1.2rem; border-radius: .5rem; border: 0; cursor: pointer; }
                  button:disabled { opacity: .6; cursor: default; }
                  form { display: inline; }
                  form.block { display: grid; gap: .6rem; max-width: 20rem; }
                  input, select { font: inherit; padding: .4rem; }
                  li { margin: .5rem 0; }
                  table { border-collapse: collapse; width: 100%; }
                  th, td { text-align: left; padding: .5rem .4rem; vertical-align: top; }
                  tbody tr { border-top: 1px solid color-mix(in srgb, currentColor 20%, transparent); }
                  code { overflow-wrap: anywhere; }
                  .notice { padding: .7rem 1rem; border-radius: .5rem; background: color-mix(in srgb, currentColor 8%, transparent); }
                </style>
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