using System.Security.Cryptography;
using System.Text;
using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Security;
using McpServerApps.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace McpServerApps.Serving;

// Who is viewing an app origin, as far as this server believes: the person of the app session the
// origin's own cookie names, which only the app sign-in opens. The accounts service vouches for who
// that is — an `apps` assertion, verified here, the one thing believed about the viewer — and this
// server alone decides what they may open (AppPages).
//
// - A page opened with no app session is sent to the app sign-in on the main host, carrying where it
//   was going (AppSignIn.ReturnTo) and a state this browser keeps in a cookie; the person comes back
//   to CallbackPath with a code and that state, the code is redeemed service to service for an
//   assertion only in the browser holding the state, and the session's cookie is set.
// - The assertion an app session holds is renewed by the session refresh once it has been held
//   AssertionKept, as the chat hub renews its connection's person: never after the session is
//   revoked (the refresh answers nobody, and the app session ends), never on the word of a session
//   that now names somebody else. An accounts service that cannot be asked serves nothing and ends
//   nothing, since an outage is no revocation.
public sealed class AppViewers(
    AppSessions sessions,
    IAppSignIns accounts,
    AssertionVerifier verifier,
    AppsSettings settings,
    TimeProvider clock,
    ILogger<AppViewers> logger)
{
    public const string StateCookieName = "__Host-app-sign-in";

    // Long enough for a passkey sign-in on the way.
    private static readonly TimeSpan _stateLifetime = TimeSpan.FromMinutes(10);

    public async Task<AppViewer> ViewerAsync(HttpRequest request, AppOrigin origin, CancellationToken ct)
    {
        if (request.Cookies[AppSessions.CookieName] is not { Length: > 0 } token
            || sessions.Find(token, origin) is not { } session)
        {
            return AppViewer.Nobody;
        }

        if (clock.GetUtcNow() < session.RenewAt)
        {
            return new AppViewer.Person(session);
        }

        ResolvedAccount? renewed;
        try
        {
            renewed = await accounts.RenewAsync(session.Person.UserId, session.SessionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not renew an app session of account {UserId}; nothing is served on it until it can be",
                session.Person.UserId);
            return AppViewer.Unchecked;
        }

        // A suspended household's person opens nothing, as the session check lets them in nowhere.
        if (renewed is null || renewed.HouseholdSuspended)
        {
            sessions.End(token);
            return AppViewer.Nobody;
        }

        // Refused here is a forgery, a clock or keys that cannot be fetched: nothing to end the session over.
        if (await verifier.VerifyAsync(renewed.Assertion, AppChannels.Apps, ct) is not { } person)
        {
            return AppViewer.Unchecked;
        }

        if (new Account(person.TenantId, person.UserId) != session.Person || person.SessionId != session.SessionId)
        {
            logger.LogWarning("A renewal of an app session of account {UserId} named somebody else; the session is ended",
                session.Person.UserId);
            sessions.End(token);
            return AppViewer.Nobody;
        }

        sessions.Renewed(token, renewed.Assertion);
        return new AppViewer.Person(session with { Assertion = renewed.Assertion });
    }

    // A page opened with no app session, sent to sign in: only with a main host to send it to and only
    // for an origin the accounts service would send a code back to, else false (and the caller's 404).
    // The sign-in is tied to this browser by a fresh state, kept in a cookie of this origin's alone and
    // carried there and back, which the callback holds the code to.
    public async Task<bool> SendToSignInAsync(HttpContext context)
    {
        if (settings.PublicBaseUrl.Length == 0 || ReturnOf(context.Request) is not { } target)
        {
            return false;
        }

        var state = AppSignIn.NewState();
        context.Response.Cookies.Append(StateCookieName, state, Cookie(_stateLifetime));
        context.Response.Headers.Location =
            $"{settings.PublicBaseUrl.TrimEnd('/')}{AppSignIn.Path}?{AppSignIn.ReturnUrlParameter}="
            + $"{Uri.EscapeDataString($"{target.Origin}{target.Path}")}&{AppSignIn.StateParameter}={state}";
        await AnswerAsync(context, StatusCodes.Status302Found);
        return true;
    }

    // The person's arrival back from the app sign-in: the code redeemed for this origin, the assertion
    // verified, the session opened and its cookie set, then on to where they were going on this origin.
    // A refused code is told as such and never sent round to sign in again, so a broken sign-in cannot
    // loop between two hosts.
    public async Task CompleteAsync(HttpContext context, AppOrigin origin)
    {
        var request = context.Request;
        var ct = context.RequestAborted;
        var code = request.Query[AppSignIn.CodeParameter].ToString();
        var state = request.Query[AppSignIn.StateParameter].ToString();
        // Checked before the code is redeemed, so a code brought to the wrong browser is not spent by it.
        if (code.Length == 0
            || !AppSignIn.IsState(state)
            || request.Cookies[StateCookieName] is not { } started
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(started), Encoding.ASCII.GetBytes(state))
            || ReturnOf(request) is not { } target)
        {
            await AnswerAsync(context, StatusCodes.Status400BadRequest, SignInRefused);
            return;
        }

        ResolvedAccount? redeemed;
        try
        {
            redeemed = await accounts.RedeemAsync(code, target.Origin, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not redeem an app sign-in code");
            await AnswerAsync(context, StatusCodes.Status503ServiceUnavailable, SignInUnchecked);
            return;
        }

        if (redeemed is null)
        {
            await AnswerAsync(context, StatusCodes.Status400BadRequest, SignInRefused);
            return;
        }

        if (await verifier.VerifyAsync(redeemed.Assertion, AppChannels.Apps, ct) is not { SessionId: { } sessionId } person)
        {
            await AnswerAsync(context, StatusCodes.Status503ServiceUnavailable, SignInUnchecked);
            return;
        }

        var lifetime = settings.Sessions.Lifetime;
        var token = sessions.Open(new Account(person.TenantId, person.UserId), sessionId, origin, redeemed.Assertion,
            clock.GetUtcNow() + lifetime);
        var response = context.Response;
        response.Cookies.Delete(StateCookieName, Cookie(TimeSpan.Zero));
        response.Cookies.Append(AppSessions.CookieName, token, Cookie(lifetime));
        response.Headers.Location = AppSignIn.SamePath(request.Query[AppSignIn.PathParameter].ToString());
        await AnswerAsync(context, StatusCodes.Status303SeeOther);
    }

    // The origin as the browser named it: the Host the proxy passes on and the scheme it says the
    // browser used, else this listener's own (a host run, on plain http).
    private AppReturn? ReturnOf(HttpRequest request)
    {
        var scheme = request.Headers["X-Forwarded-Proto"].ToString() is { Length: > 0 } forwarded ? forwarded : request.Scheme;
        return AppSignIn.ReturnTo($"{scheme}://{request.Host.Value}{request.PathBase}{request.Path}{request.QueryString}",
            settings.AppsDomain);
    }

    // Host-only and `__Host-`, so no other origin under the apps domain — another app's code — can set
    // or read it. Lax, not Strict: every arrival on the callback ends a hop from the main host, another
    // site, and a Strict cookie is not sent on such a navigation, nor on the one after it.
    private static CookieOptions Cookie(TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = lifetime == TimeSpan.Zero ? null : lifetime,
        IsEssential = true
    };

    public static Task AnswerAsync(HttpContext context, int status, string? page = null)
    {
        var response = context.Response;
        response.StatusCode = status;
        response.Headers.ContentSecurityPolicy = AppContentPolicy.Closed;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        if (page is null)
        {
            return Task.CompletedTask;
        }

        var body = Encoding.UTF8.GetBytes(page);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength = body.Length;
        return response.Body.WriteAsync(body, context.RequestAborted).AsTask();
    }

    // Plain and self-contained, as the closed policy loads nothing.
    private const string SignInRefused =
        """
        <!doctype html>
        <html lang="en">
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Sign-in ran out</title>
        <h1>That sign-in ran out</h1>
        <p>It was used already or waited too long. <a href="/">Open the app again</a>.</p>
        </html>
        """;

    public const string SignInUnchecked =
        """
        <!doctype html>
        <html lang="en">
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Could not check who you are</title>
        <h1>Could not check who you are right now</h1>
        <p>Nothing is wrong with your sign-in. Try again in a moment.</p>
        </html>
        """;
}

// Who an app origin's request is from: a person by their app session, nobody, or somebody whose
// session the accounts service could not be asked about just now.
public abstract record AppViewer
{
    public static readonly AppViewer Nobody = new NoViewer();
    public static readonly AppViewer Unchecked = new UncheckedViewer();

    public sealed record Person(AppSession Session) : AppViewer;

    private sealed record NoViewer : AppViewer;

    private sealed record UncheckedViewer : AppViewer;
}