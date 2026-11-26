using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.Security;
using Domain.Tools;
using McpServerApps.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace McpServerApps.Serving;

// A built app's read handed to the agent, which reads it (AppReads.AgentPath): this server's own
// credential, which the agent holds for the apps server it dials, carries the app — its id, building
// agent and the origin's reads — and the viewer's current `apps` assertion says who reads, or, on a
// check's session, the agent's own token for the call that ran the check. The agent's
// answer comes back as it was: the tool's result (200), a file's bytes (200) or the tools' envelope (422).
// An agent that cannot be reached, or that did not believe this server, is the unavailable envelope.
public sealed class AgentReads(HttpClient http, AppsSettings settings, ILogger<AgentReads> logger)
{
    public async Task<OriginAnswer> ReadAsync(
        string operation, AppReadScope app, JsonObject arguments, ViewerProof viewer, CancellationToken ct)
    {
        if (settings.AgentUrl.Length == 0)
        {
            return Unavailable("this server has no agent to read through");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.AgentUrl.TrimEnd('/')}{AppReads.AgentRoute(operation)}")
        {
            Content = JsonContent.Create(new AppReadRequest { App = app, Arguments = arguments })
        };
        request.Headers.Add("Authorization", SharedSecret.Header(settings.Mcp.SharedSecret));
        switch (viewer)
        {
            case ViewerProof.Assertion { Token: var assertion }:
                request.Headers.Add(ForwardedAccount.AssertionHeader, assertion);
                break;
            case ViewerProof.CheckCall { Token: var callToken }:
                request.Headers.Add(AppReads.CheckCallTokenHeader, callToken);
                break;
        }
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity))
            {
                logger.LogWarning("The agent answered app {AppId}'s {Operation} read with {Status}", app.Id, operation, response.StatusCode);
                return Unavailable($"the agent answered {(int)response.StatusCode}");
            }

            return new OriginAnswer(
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
                await response.Content.ReadAsByteArrayAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not reach the agent for app {AppId}'s {Operation} read", app.Id, operation);
            return Unavailable("the agent could not be reached");
        }
    }

    private static OriginAnswer Unavailable(string why) =>
        OriginAnswer.Envelope(StatusCodes.Status503ServiceUnavailable, ToolError.TransientDependency(
            $"The household could not be read just now: {why}. Nothing was read.", "Read again in a moment."));
}

// What the origin answers a page's read or ask with, as it came from the agent: a body under its type, or
// the tools' envelope.
public sealed record OriginAnswer(int Status, string ContentType, byte[] Body)
{
    public static OriginAnswer Envelope(int status, ToolErrorResult error) => Json(status, error.ToNode());

    public static OriginAnswer Json(int status, System.Text.Json.Nodes.JsonNode body) =>
        new(status, "application/json; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()));
}