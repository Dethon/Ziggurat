using System.Net;
using Accounts.Links;
using Accounts.Passkeys;

namespace Accounts.Pages;

// The service's own pages: a link's enrollment and sign-in. Server-rendered, with one small script
// for navigator.credentials, so they need nothing of the apps they let people into.
public static class AccountsPages
{
    public const string ScriptPath = "/passkeys.js";

    // The script is the only one the pages run, and nothing else is fetched: the enrollment page's
    // address carries its link's token.
    private const string Policy =
        "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'unsafe-inline'; "
        + "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

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

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static IResult Page(HttpContext http, string title, string body, int status = StatusCodes.Status200OK)
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
                  button { font: inherit; padding: .7rem 1.2rem; border-radius: .5rem; border: 0; cursor: pointer; }
                  button:disabled { opacity: .6; cursor: default; }
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