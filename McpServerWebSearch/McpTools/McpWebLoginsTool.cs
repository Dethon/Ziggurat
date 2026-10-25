using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Channels;
using Domain.Tools;
using Domain.Tools.Web;
using Infrastructure.Utils;
using McpServerWebSearch.Handoff;
using McpServerWebSearch.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

// The model can hand a person the page where they see and revoke their logins; it cannot read
// that list or forget a login itself, so a page cannot talk it into revoking a session or covering
// its tracks.
[McpServerToolType]
public class McpWebLoginsTool(IdentityMap identities, LoginsPages pages, HandoffConfiguration settings)
{
    public const string Name = "web_logins";

    [McpServerTool(Name = Name)]
    [Description("Answers a one-time link to a page where the person sees the sites the agent is logged into as them, and forgets any of them.")]
    public async Task<CallToolResult> Run(RequestContext<CallToolRequestParams> context, CancellationToken ct = default)
    {
        switch (identities.Resolve(ConversationScope.Parse(context.Params?.Meta)))
        {
            case PersonResolution.Refused refused:
                return ToolResponse.Create(refused.Error);
            case PersonResolution.Found found:
                return await LinkForAsync(found.Person);
            default:
                throw new InvalidOperationException("A resolution is found or refused.");
        }
    }

    private async Task<CallToolResult> LinkForAsync(string person)
    {

        if (string.IsNullOrWhiteSpace(settings.PublicBaseUrl))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "The logins page is not set up on this deployment: it has no public address for the link."));
        }

        var token = await pages.MintAsync(person);
        return ToolResponse.Create(new JsonObject
        {
            ["status"] = "link_ready",
            ["link"] = pages.LinkFor(token),
            ["expiresInMinutes"] = settings.TicketMinutes,
            ["hint"] = "Give the person this link. It opens once; only they can see or forget their logins there."
        });
    }
}