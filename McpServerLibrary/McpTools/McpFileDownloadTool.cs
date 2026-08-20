using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Channels;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Downloads;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpFileDownloadTool(
    IDownloadClient client,
    ISearchResultsManager searchResultsManager,
    IDownloadRoutingStore routingStore,
    DownloadPathConfig pathConfig)
    : FileDownloadTool(client, searchResultsManager, routingStore, pathConfig)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        RequestContext<CallToolRequestParams> context,
        [Description("Id from a prior file_search result. Mutually exclusive with link — when set, omit link and title entirely.")]
        int? searchResultId,
        [Description("Magnet URI or http(s) .torrent URL obtained from any other tool. Requires title. Mutually exclusive with searchResultId — omit when downloading by searchResultId; never pass placeholders like \"\" or \"null\".")]
        string? link,
        [Description("Descriptive title for the download — required when link is provided, ignored otherwise. Use the real release/display name, never placeholders like \"null\".")]
        string? title,
        CancellationToken cancellationToken)
    {
        if (!ConversationScope.TryResolve(context.Params?.Meta, out var sessionId))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot scope search results."));
        }

        var normalizedLink = NormalizeOptionalText(link);
        var normalizedTitle = NormalizeOptionalText(title);

        var validation = ValidateInputs(searchResultId, normalizedLink, normalizedTitle);
        if (validation is not null)
        {
            return ToolResponse.Create(validation);
        }

        var conversationContext = ConversationScope.Parse(context.Params?.Meta);

        var result = searchResultId.HasValue
            ? await Run(sessionId, searchResultId.Value, conversationContext, cancellationToken)
            : await Run(sessionId, normalizedLink!, normalizedTitle!, conversationContext, cancellationToken);

        return ToolResponse.Create(result);
    }

    private static readonly string[] _placeholderValues = ["null", "undefined"];

    public static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return _placeholderValues.Contains(trimmed, StringComparer.OrdinalIgnoreCase) ? null : trimmed;
    }

    public static JsonNode? ValidateInputs(int? searchResultId, string? link, string? title)
    {
        var normalizedLink = NormalizeOptionalText(link);
        var normalizedTitle = NormalizeOptionalText(title);
        var hasId = searchResultId.HasValue;
        var hasLink = normalizedLink is not null;

        if (hasId && hasLink)
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Provide either searchResultId or link, not both. To download a file_search result, " +
                "pass only its searchResultId and omit link and title.");
        }

        if (!hasId && !hasLink)
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Provide either searchResultId or link.");
        }

        if (hasLink && normalizedTitle is null)
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "title is required when link is provided. Use the real release/display name from " +
                "where the link was found, never placeholders like \"null\".");
        }

        if (hasLink && !IsAcceptedLink(normalizedLink!))
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "link must start with magnet:, http://, or https://.");
        }

        return null;
    }

    private static bool IsAcceptedLink(string link)
    {
        return link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
               || link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}