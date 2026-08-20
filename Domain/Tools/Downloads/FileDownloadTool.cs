using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;

namespace Domain.Tools.Downloads;

public class FileDownloadTool(
    IDownloadClient client,
    ISearchResultsManager searchResultsManager,
    IDownloadRoutingStore routingStore,
    DownloadPathConfig pathConfig,
    TimeProvider? timeProvider = null)
{
    protected const string Name = "download_file";

    protected const string Description = """
                                         Download a file from the internet.

                                         Provide EXACTLY ONE of:
                                           - searchResultId: an id from a prior file_search call. Omit link and title entirely —
                                             do not fill them with "", "null", or any other placeholder.
                                           - link + title: a magnet URI or http(s) .torrent URL obtained from any other tool, plus
                                             a descriptive title (e.g. the release name with quality and group, taken from wherever
                                             the link was found). Both must be real values, never "null" or empty strings.

                                         Never provide both a searchResultId and a link. The link path is intended as a fallback
                                         when file_search returns no usable results.
                                         """;

    protected async Task<JsonNode> Run(string sessionId, int searchResultId, ConversationContext? context, CancellationToken ct)
    {
        var existing = await client.GetDownloadItem(searchResultId, ct);
        if (existing is not null)
        {
            return ToolError.Create(
                ToolError.Codes.AlreadyExists,
                "Download with this id already exists, try another id");
        }

        var itemToDownload = searchResultsManager.Get(sessionId, searchResultId);
        if (itemToDownload == null)
        {
            return ToolError.Create(
                ToolError.Codes.NotFound,
                $"No search result found for id {searchResultId}. " +
                "Make sure to run the file_search tool first and use the correct id.");
        }

        return await StartDownload(searchResultId, itemToDownload.Link, itemToDownload.Title, context, ct);
    }

    protected async Task<JsonNode> Run(string sessionId, string link, string title, ConversationContext? context, CancellationToken ct)
    {
        var id = link.GetHashCode();

        var existing = await client.GetDownloadItem(id, ct);
        if (existing is not null)
        {
            return ToolError.Create(
                ToolError.Codes.AlreadyExists,
                "Download with this link already exists, choose a different link");
        }

        var synthetic = new SearchResult
        {
            Id = id,
            Title = title,
            Link = link
        };
        searchResultsManager.Add(sessionId, [synthetic]);

        return await StartDownload(id, link, title, context, ct);
    }

    private async Task<JsonNode> StartDownload(int id, string link, string title, ConversationContext? context, CancellationToken ct)
    {
        var savePath = $"{pathConfig.BaseDownloadPath}/{id}";
        await client.Download(link, savePath, id, ct);

        if (context is not null)
        {
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            await routingStore.SetAsync(new DownloadRouting
            {
                DownloadId = id,
                Title = title,
                Context = context,
                SubmittedAt = now
            }, ct);
            return new JsonObject
            {
                ["status"] = "success",
                ["message"] = $"Download with id {id} started successfully. A completion message will arrive in this conversation when it finishes."
            };
        }

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = $"Download with id {id} started successfully. No conversation context was provided, " +
                          $"so no completion alert will fire; check {MediaFilesystem.DownloadsDir} for status."
        };
    }
}