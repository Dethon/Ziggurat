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

// Required is always true here, unlike in the standalone app, which ran open in Development: a code
// nobody set refuses every sign-in. The field stays because the app reads it.
//
// Estimates and Labels say which of the app's model-backed actions this server can answer. Both are
// off until the sugary agent answers them (spec tickets 04 and 07), and the app shows them as not
// available yet rather than letting them fail.
public record SessionState(bool Required, bool Authenticated, bool Estimates, bool Labels);

public static class SessionEndpoints
{
    public const string SignInRateLimit = "sign-in";

    public static void MapSessionEndpoints(this IEndpointRouteBuilder api)
    {
        var session = api.MapGroup("/session").AllowAnonymous();
        session.MapGet("/", (HttpContext http) =>
            new SessionState(true, http.User.Identity?.IsAuthenticated is true, Estimates: false, Labels: false));
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