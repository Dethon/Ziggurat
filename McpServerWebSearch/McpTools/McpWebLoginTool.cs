using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Channels;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Web;
using Infrastructure.Utils;
using McpServerWebSearch.Handoff;
using McpServerWebSearch.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebLoginTool(IdentityMap identities, LoginHandoffs handoffs, HandoffConfiguration settings)
{
    [McpServerTool(Name = WebLoginTool.Name)]
    [Description(WebLoginTool.Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("The site's login page, or any page of the site that leads to it (http or https)")]
        string url,
        CancellationToken ct = default)
    {
        if (ConversationScope.Parse(context.Params?.Meta) is not { } conversation)
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot tell who is logging in."));
        }

        if (WebLoginTool.RefuseFire(conversation) is { } fire)
        {
            return ToolResponse.Create(fire);
        }

        var resolution = identities.Resolve(conversation);
        if (resolution is PersonResolution.Refused refused)
        {
            return ToolResponse.Create(refused.Error);
        }

        var person = ((PersonResolution.Found)resolution).Person;
        if (string.IsNullOrWhiteSpace(settings.PublicBaseUrl))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "Login handoffs are not set up on this deployment: it has no public address for the link.",
                "Tell the person their account cannot be reached until the operator sets the handoff address."));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument, "Only an http or https url can be handed off."));
        }

        LoginHandoffs.LiveHandoff handoff;
        try
        {
            handoff = await handoffs.BeginAsync(person, conversation, uri.ToString(), ct);
        }
        catch (PersonStateUnreadableException ex)
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.UnsupportedOperation, ex.Message,
                "Tell the person their saved logins cannot be opened on this server until the operator fixes the key."));
        }

        return ToolResponse.Create(new JsonObject
        {
            ["status"] = "handoff_started",
            ["link"] = handoffs.LinkFor(handoff.Token),
            ["site"] = uri.Host,
            ["expiresInMinutes"] = settings.TicketMinutes,
            ["hint"] = "Give the person this link and stop. It works once; they log in on it and press "
                       + "Done, and you are woken in this conversation to carry on as them."
        });
    }
}