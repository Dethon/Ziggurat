using System.Security.Claims;
using Domain.Security;
using McpServerSugary.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace McpServerSugary.App;

public record SignInRequest(string? Code);

// Required is true unless the deployment switched the code off (RequireAccessCode, for a server reached
// only through a VPN). With it on, a code nobody set refuses every sign-in.
//
// Estimates and Labels say which of the app's model-backed actions this server can answer. Both are
// turns of the sugary agent now; the app still reads the flags, and shows an action that is off as
// not available rather than letting it fail.
public record SessionState(bool Required, bool Authenticated, bool Estimates, bool Labels);

public static class SessionEndpoints
{
    public const string SignInRateLimit = "sign-in";

    public static void MapSessionEndpoints(this IEndpointRouteBuilder api)
    {
        var session = api.MapGroup("/session").AllowAnonymous();
        session.MapGet("/", (HttpContext http, SugaryAppSettings settings) => new SessionState(
            settings.RequireAccessCode,
            !settings.RequireAccessCode || http.User.Identity?.IsAuthenticated is true,
            Estimates: true,
            Labels: true));
        session.MapPost("/", SignInAsync).RequireRateLimiting(SignInRateLimit);
        session.MapDelete("/", async (HttpContext http) =>
        {
            await http.SignOutAsync();
            return Results.NoContent();
        });
    }

    private static async Task<IResult> SignInAsync(SignInRequest request, HttpContext http, SugaryAppSettings settings)
    {
        // The one comparison every shared-secret gate uses. A code is typed on a phone keyboard, so
        // the spaces around it are not part of it.
        if (!SharedSecret.Matches(SharedSecret.Header(request.Code?.Trim() ?? ""), settings.AccessCode.Trim()))
        {
            return Results.Problem("Wrong access code.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "sugary")], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
        return Results.NoContent();
    }
}