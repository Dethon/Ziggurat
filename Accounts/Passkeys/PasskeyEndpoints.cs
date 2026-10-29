using System.Text.Json;
using Accounts.Pages;
using Accounts.Sessions;
using Accounts.Settings;
using Fido2NetLib;

namespace Accounts.Passkeys;

// The calls the enrollment and sign-in pages' script makes: each ceremony's options, then the
// authenticator's answer. A finished ceremony sets the session cookie and says where to go next.
public static class PasskeyEndpoints
{
    public const string RefusedLink = "This link can no longer be used.";
    public const string UnknownPasskey = "That passkey is not recognised here.";

    public sealed record EnrollmentOptionsRequest(string Token);

    public sealed record EnrollmentRequest(string Ceremony, AuthenticatorAttestationRawResponse Credential);

    public sealed record SignInRequest(string Ceremony, AuthenticatorAssertionRawResponse Credential, string? ReturnUrl);

    public static void Map(IEndpointRouteBuilder accounts)
    {
        var api = accounts.MapGroup("/api");

        api.MapPost("/enroll/options", async (EnrollmentOptionsRequest request, PasskeyCeremonies ceremonies, CancellationToken ct) =>
            await ceremonies.BeginEnrollmentAsync(request.Token, ct) is { } begun
                ? Begun(begun)
                : Refused(StatusCodes.Status410Gone, RefusedLink));

        api.MapPost("/enroll", async (
            EnrollmentRequest request, HttpContext http, PasskeyCeremonies ceremonies, AccountsSettings settings,
            CancellationToken ct) =>
        {
            var session = await ceremonies.FinishEnrollmentAsync(
                request.Ceremony, request.Credential, http.Request.Headers.UserAgent.ToString(), ct);
            if (session is null)
            {
                return Refused(StatusCodes.Status400BadRequest, "The passkey was not added. Open the link again to retry.");
            }

            SessionCookie.Write(http.Response, settings, session.Token);
            return Results.Json(new { redirect = ReturnUrls.Resolve(settings, null) });
        });

        api.MapPost("/sign-in/options", (PasskeyCeremonies ceremonies) => Begun(ceremonies.BeginSignIn()));

        api.MapPost("/sign-in", async (
            SignInRequest request, HttpContext http, PasskeyCeremonies ceremonies, AccountsSettings settings,
            CancellationToken ct) =>
        {
            var session = await ceremonies.FinishSignInAsync(
                request.Ceremony, request.Credential, http.Request.Headers.UserAgent.ToString(), ct);
            if (session is null)
            {
                return Refused(StatusCodes.Status401Unauthorized, UnknownPasskey);
            }

            SessionCookie.Write(http.Response, settings, session.Token);
            return Results.Json(new { redirect = ReturnUrls.Resolve(settings, request.ReturnUrl) });
        });
    }

    // The options go out as Fido2's own JSON, which is WebAuthn's encoding of them.
    private static IResult Begun(BegunCeremony begun) =>
        Results.Content(
            $$"""{"ceremony":{{JsonSerializer.Serialize(begun.Id)}},"options":{{begun.OptionsJson}}}""",
            "application/json");

    private static IResult Refused(int status, string error) => Results.Json(new { error }, statusCode: status);
}