using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Tools.Config;
using Infrastructure.StateManagers;
using Infrastructure.Utils;
using McpServerLibrary.McpTools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Search results used to be namespaced by the MCP session id. The 2026-07-28 protocol removed
// sessions, so the namespace is now the conversation carried in each call's _meta. These tests
// pin the two properties that matter: one conversation cannot see another's results, and a call
// with no conversation context is rejected instead of silently sharing (or severing) a bucket.
public class McpLibraryConversationScopeTests : IAsyncLifetime
{
    private const string MagnetA = "magnet:?xt=urn:btih:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&dn=result-a";

    private WebApplication _app = null!;
    private MemoryCache _cache = null!;
    private RecordingDownloadClient _downloads = null!;
    private string _endpoint = null!;

    public async Task InitializeAsync()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
        _downloads = new RecordingDownloadClient();
        var port = TestPort.GetAvailable();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services
            .AddSingleton(new DownloadPathConfig(Path.Combine(Path.GetTempPath(), "mcp-library-scope")))
            .AddSingleton<IMemoryCache>(_cache)
            .AddSingleton<ISearchResultsManager, SearchResultsManager>()
            .AddSingleton<IDownloadRoutingStore, InMemoryDownloadRoutingStore>()
            .AddSingleton<ISearchClient>(new StubSearchClient())
            .AddSingleton<IDownloadClient>(_downloads)
            .AddMcpServer()
            .WithHttpTransport()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
            {
                try
                {
                    return await next(context, ct);
                }
                catch (Exception ex)
                {
                    return ToolResponse.Create(ex);
                }
            }))
            .WithTools<McpFileSearchTool>()
            .WithTools<McpFileDownloadTool>();

        _app = builder.Build();
        _app.MapMcp("/mcp");
        await _app.StartAsync();

        _endpoint = $"http://localhost:{port}/mcp";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _cache.Dispose();
    }

    [Fact]
    public async Task SearchResultsOfOneConversation_AreInvisibleToAnother()
    {
        await using var client = await CreateClientAsync();
        var tools = await client.ListToolsAsync();
        var search = tools.Single(t => t.Name == "file_search");
        var download = tools.Single(t => t.Name == "download_file");

        var searchResult = await search.WithMeta(MetaFor("nabu", "conv-a")).CallAsync(
            new Dictionary<string, object?> { ["searchStrings"] = new[] { "anything" } });
        var id = FirstResultId(searchResult);

        var foreign = await download.WithMeta(MetaFor("nabu", "conv-b")).CallAsync(
            new Dictionary<string, object?>
            {
                ["searchResultId"] = id,
                ["link"] = null,
                ["title"] = null
            });

        Text(foreign).ShouldContain("not_found");
        Text(foreign).ShouldContain($"No search result found for id {id}");
        _downloads.Started.ShouldBeEmpty();

        var own = await download.WithMeta(MetaFor("nabu", "conv-a")).CallAsync(
            new Dictionary<string, object?>
            {
                ["searchResultId"] = id,
                ["link"] = null,
                ["title"] = null
            });

        Text(own).ShouldContain("success");
        _downloads.Started.ShouldContain(MagnetA);
    }

    [Fact]
    public async Task SearchResultsOfOneAgent_AreInvisibleToAnotherAgentOnTheSameConversationId()
    {
        await using var client = await CreateClientAsync();
        var tools = await client.ListToolsAsync();
        var search = tools.Single(t => t.Name == "file_search");
        var download = tools.Single(t => t.Name == "download_file");

        var searchResult = await search.WithMeta(MetaFor("nabu", "shared-id")).CallAsync(
            new Dictionary<string, object?> { ["searchStrings"] = new[] { "anything" } });
        var id = FirstResultId(searchResult);

        var foreign = await download.WithMeta(MetaFor("jack", "shared-id")).CallAsync(
            new Dictionary<string, object?>
            {
                ["searchResultId"] = id,
                ["link"] = null,
                ["title"] = null
            });

        Text(foreign).ShouldContain("not_found");
    }

    [Fact]
    public async Task FileSearch_WithoutConversationContext_ReturnsInvalidArgument()
    {
        await using var client = await CreateClientAsync();

        var result = await client.CallToolAsync(
            "file_search",
            new Dictionary<string, object?> { ["searchStrings"] = new[] { "anything" } });

        Text(result).ShouldContain("invalid_argument");
        Text(result).ShouldContain("Conversation context is missing");
    }

    [Fact]
    public async Task DownloadFile_WithoutConversationContext_ReturnsInvalidArgument()
    {
        await using var client = await CreateClientAsync();

        var result = await client.CallToolAsync(
            "download_file",
            new Dictionary<string, object?>
            {
                ["searchResultId"] = null,
                ["link"] = MagnetA,
                ["title"] = "Result A"
            });

        Text(result).ShouldContain("invalid_argument");
        Text(result).ShouldContain("Conversation context is missing");
        _downloads.Started.ShouldBeEmpty();
    }

    private Task<McpClient> CreateClientAsync() => McpClient.CreateAsync(
        McpTestSecret.Transport(_endpoint));

    private static JsonObject MetaFor(string agentId, string conversationId)
    {
        var context = new ConversationContext(
            agentId, conversationId, "fran", new ReplyTarget("signalr", conversationId));
        return new JsonObject
        {
            [ChannelProtocol.ConversationContextMetaKey] =
                JsonSerializer.SerializeToNode(context, ChannelProtocol.SerializerOptions)
        };
    }

    private static string Text(CallToolResult result) => result.Content
        .OfType<TextContentBlock>()
        .Select(t => t.Text)
        .FirstOrDefault() ?? "";

    private static int FirstResultId(CallToolResult result)
    {
        var payload = JsonNode.Parse(Text(result)).ShouldNotBeNull();
        var first = payload["results"].ShouldNotBeNull().AsArray()[0].ShouldNotBeNull().AsObject();
        return first
            .First(p => string.Equals(p.Key, "id", StringComparison.OrdinalIgnoreCase))
            .Value!.GetValue<int>();
    }

    private sealed class StubSearchClient : ISearchClient
    {
        public Task<SearchResult[]> Search(string query, CancellationToken cancellationToken = default)
            => Task.FromResult<SearchResult[]>([
                new SearchResult { Id = MagnetA.GetHashCode(), Title = "Result A", Link = MagnetA }
            ]);
    }

    private sealed class RecordingDownloadClient : IDownloadClient
    {
        public ConcurrentBag<string> Started { get; } = [];

        public Task Download(string link, string savePath, int id, CancellationToken cancellationToken = default)
        {
            Started.Add(link);
            return Task.CompletedTask;
        }

        public Task Cleanup(int id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DownloadItem?> GetDownloadItem(int id, CancellationToken cancellationToken = default)
            => Task.FromResult<DownloadItem?>(null);

        public Task<IReadOnlyList<DownloadItem>> GetDownloadItems(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DownloadItem>>([]);
    }
}