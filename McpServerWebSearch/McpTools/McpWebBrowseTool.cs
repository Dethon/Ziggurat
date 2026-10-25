using System.ComponentModel;
using Domain.Channels;
using Domain.Contracts;
using Domain.Judgments;
using Domain.Tools;
using Domain.Tools.Web;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebBrowseTool(IWebBrowser browser, IdentityMap identities)
    : WebBrowseTool(browser)
{
    [McpServerTool(Name = Name)]
    [McpMeta(FootprintTemplate.MetaKey, JsonValue = BrowserFootprint.Page)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("The URL to navigate to")]
        string url,
        [Description("Maximum characters to return (100-100000, default: 10000)")]
        int maxLength = 10000,
        [Description("Character offset to start from (0 for the beginning; to page forward, pass the nextOffset the previous call returned)")]
        int offset = 0,
        [Description("CSS selector to extract specific elements (e.g. '.product-card', '#main'). Returns ALL matches.")]
        string? selector = null,
        [Description("Extract clean article content, stripping ads, navigation, sidebars")]
        bool useReadability = false,
        [Description("Scroll page to trigger lazy-loaded content")]
        bool scrollToLoad = false,
        [Description("Number of scroll steps for lazy loading (1-10, default: 3)")]
        int scrollSteps = 3,
        [Description("Include the accessibility tree (snapshot) in the same call. Use when you intend to interact with the page; saves a separate web_snapshot round trip.")]
        bool snapshot = false,
        [Description("Browse as the person you are talking to, carrying their own logins. Only when the task needs their account; without it the browse carries nobody's cookies.")]
        bool asMe = false,
        CancellationToken ct = default)
    {
        if (!ConversationScope.TryResolve(context.Params?.Meta, out var sessionId))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot scope the browser session."));
        }

        var conversation = ConversationScope.Parse(context.Params?.Meta);
        string? person = null;
        if (asMe)
        {
            switch (identities.Resolve(conversation))
            {
                case PersonResolution.Found found:
                    person = found.Person;
                    break;
                case PersonResolution.Refused refused:
                    return ToolResponse.Create(refused.Error);
            }
        }

        var caller = JudgmentCaller.For(conversation);
        var result = await RunAsync(sessionId, caller, person, url, selector, maxLength, offset,
            useReadability, scrollToLoad, scrollSteps, snapshot, ct);
        return result.Body is null
            ? ToolResponse.Create(result.Envelope)
            : ToolResponse.Create(result.Envelope, result.Body, result.Snapshot);
    }
}