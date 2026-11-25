using System.Text.Json;
using Domain.Apps;
using Domain.DTOs;
using Domain.Security;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;

namespace Agent.App;

// A built app's reads, forwarded by the apps server, its one caller. On the deployment's network alone:
// the proxy routes nothing under /internal (DeploymentConformanceTests). Two things are believed and
// nothing else: the apps server's own credential, the one the agent already holds for the apps server
// it dials (sharedServers.apps), which carries the app — its id, building agent and reads; and the
// viewer's `apps` assertion, which alone says who reads, of which household.
//
// A value answers 200 (the tool's JSON, or a blob's bytes); the tools' envelope answers 422; a caller
// not believed, 401.
public static class AppReadsApi
{
    public static void MapAppReads(this IEndpointRouteBuilder app) =>
        app.MapPost($"{AppReads.AgentPath}/{{operation}}", async (
            HttpContext http, string operation, McpCredentials credentials, SharedServers shared, AppReader reader) =>
        {
            var ct = http.RequestAborted;
            if (!FromTheAppsServer(http.Request, credentials, shared))
            {
                return Results.Unauthorized();
            }

            // Read only once the caller is believed, so nothing it sent is looked at before.
            AppReadRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<AppReadRequest>(ct);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (request is null)
            {
                return Results.BadRequest();
            }

            return await reader.ReadAsync(AssertionVerifier.AssertionOf(http.Request.Headers), operation, request, ct) switch
            {
                null => Results.Unauthorized(),
                AppReadAnswer.Bytes bytes => Results.Bytes(bytes.Content, "application/octet-stream"),
                AppReadAnswer.Json { IsRefusal: true } refused =>
                    Results.Json(refused.Node, statusCode: StatusCodes.Status422UnprocessableEntity),
                AppReadAnswer.Json json => Results.Json(json.Node),
                _ => throw new InvalidOperationException("A read answers a value, bytes or a refusal.")
            };
        });

    // The credential the agent holds for the apps server it dials, and none where it dials none.
    private static bool FromTheAppsServer(HttpRequest request, McpCredentials credentials, SharedServers shared) =>
        shared.Apps is { } apps
        && ServerAddress.FromUrl(apps) is { } server
        && credentials.For(server) is { } credential
        && SharedSecret.Matches(request.Headers.Authorization.ToString(), credential);
}