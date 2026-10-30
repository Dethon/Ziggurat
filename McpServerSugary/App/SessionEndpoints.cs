using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace McpServerSugary.App;

// Estimates and Labels say which of the app's model-backed actions this server can answer. Both are
// turns of the sugary agent now; the app still reads the flags, and shows an action that is off as
// not available rather than letting it fail.
public record SessionState(bool Estimates, bool Labels);

// The first thing the app asks. Behind the proxy's headers like every route, so a phone whose
// session is gone learns it here, from the 401, and goes to sign in.
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this IEndpointRouteBuilder api) =>
        api.MapGet("/session", () => new SessionState(Estimates: true, Labels: true));
}