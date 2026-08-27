using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Browser;
using McpServerWebSearch.Modules;
using McpServerWebSearch.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Playwright;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Browser sessions used to be keyed on the MCP session id, which the 2026-07-28 protocol removed:
// RequireSessionId() then threw on every browse/snapshot/action call. The key is now the
// conversation carried in each call's _meta, so a conversation keeps its cookies and element refs
// across calls while a different conversation gets its own page.
public class McpWebSearchSessionScopeTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private RecordingBrowser _browser = null!;
    private string _endpoint = null!;

    public async Task InitializeAsync()
    {
        _browser = new RecordingBrowser();
        var port = TestPort.GetAvailable();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureMcp(new McpSettings
        {
            BraveSearch = new BraveSearchConfiguration { ApiKey = "test" },
            Camoufox = null,
            CapSolver = null
        });
        builder.Services.RemoveAll<IWebBrowser>();
        builder.Services.AddSingleton<IWebBrowser>(_browser);

        _app = builder.Build();
        _app.MapMcp("/mcp");
        await _app.StartAsync();

        _endpoint = $"http://localhost:{port}/mcp";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task WebBrowse_KeysTheBrowserSessionOnTheConversation()
    {
        await using var client = await CreateClientAsync();
        var browse = (await client.ListToolsAsync()).Single(t => t.Name == "web_browse");

        await Browse(browse, "nabu", "conv-a");
        await Browse(browse, "nabu", "conv-a");
        await Browse(browse, "nabu", "conv-b");

        _browser.BrowseSessionIds.Count.ShouldBe(3);
        _browser.BrowseSessionIds.Distinct().Count().ShouldBe(2);
        _browser.BrowseSessionIds.ShouldContain("nabu:conv-a");
        _browser.BrowseSessionIds.ShouldContain("nabu:conv-b");
    }

    [Fact]
    public async Task WebSnapshotAndWebAction_ShareTheConversationsSession()
    {
        await using var client = await CreateClientAsync();
        var tools = await client.ListToolsAsync();

        await Browse(tools.Single(t => t.Name == "web_browse"), "nabu", "conv-c");
        await tools.Single(t => t.Name == "web_snapshot")
            .WithMeta(MetaFor("nabu", "conv-c"))
            .CallAsync(new Dictionary<string, object?>());
        await tools.Single(t => t.Name == "web_action")
            .WithMeta(MetaFor("nabu", "conv-c"))
            .CallAsync(new Dictionary<string, object?> { ["ref"] = "e-1" });

        _browser.BrowseSessionIds.ShouldHaveSingleItem().ShouldBe("nabu:conv-c");
        _browser.SnapshotSessionIds.ShouldHaveSingleItem().ShouldBe("nabu:conv-c");
        _browser.ActionSessionIds.ShouldHaveSingleItem().ShouldBe("nabu:conv-c");
    }

    [Theory]
    [InlineData("web_browse")]
    [InlineData("web_snapshot")]
    [InlineData("web_action")]
    public async Task BrowseTools_WithoutConversationContext_ReturnInvalidArgument(string toolName)
    {
        await using var client = await CreateClientAsync();

        var result = await client.CallToolAsync(
            toolName,
            new Dictionary<string, object?> { ["url"] = "https://example.com" });

        Text(result).ShouldContain("invalid_argument");
        Text(result).ShouldContain("Conversation context is missing");
        _browser.BrowseSessionIds.ShouldBeEmpty();
        _browser.SnapshotSessionIds.ShouldBeEmpty();
        _browser.ActionSessionIds.ShouldBeEmpty();
    }

    // Replaces the DELETE /mcp cleanup hook the stateless protocol removed: a conversation's page
    // survives across calls, and the idle timer -- which was always the real reclamation path --
    // still reclaims it.
    [Fact]
    public async Task ConversationKeyedSessions_AreReusedAcrossCalls_AndReclaimedByPruneIdle()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var page = new Mock<IPage>();
        page.SetupGet(p => p.IsClosed).Returns(false);
        page.Setup(p => p.CloseAsync(It.IsAny<PageCloseOptions?>())).Returns(Task.CompletedTask);
        var context = new Mock<IBrowserContext>();
        context.Setup(c => c.NewPageAsync()).ReturnsAsync(page.Object);

        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30));

        var scopeA = ConversationScope.Build("nabu", "conv-a");
        var first = await manager.AcquireTabForBrowseAsync(scopeA, "https://a.test/", context.Object);
        var second = await manager.AcquireTabForBrowseAsync(scopeA, "https://a.test/", context.Object);

        second.Tab.ShouldBeSameAs(first.Tab);
        context.Verify(c => c.NewPageAsync(), Times.Once);

        time.Advance(TimeSpan.FromMinutes(31));
        await manager.PruneIdleAsync();

        manager.Get(scopeA).ShouldBeNull();
        page.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
    }

    private Task<McpClient> CreateClientAsync() => McpClient.CreateAsync(
        new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(_endpoint) }));

    private static async Task<CallToolResult> Browse(
        McpClientTool browse, string agentId, string conversationId)
        => await browse.WithMeta(MetaFor(agentId, conversationId)).CallAsync(
            new Dictionary<string, object?> { ["url"] = "https://example.com" });

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

    private sealed class RecordingBrowser : IWebBrowser
    {
        public ConcurrentBag<string> BrowseSessionIds { get; } = [];
        public ConcurrentBag<string> SnapshotSessionIds { get; } = [];
        public ConcurrentBag<string> ActionSessionIds { get; } = [];

        public Task<BrowseResult> NavigateAsync(BrowseRequest request, CancellationToken ct = default)
        {
            BrowseSessionIds.Add(request.SessionId);
            return Task.FromResult(new BrowseResult(
                request.SessionId, request.Url, BrowseStatus.Success,
                "Example", "content", 7, false, null, null, null, null));
        }

        public Task<BrowseResult> GetCurrentPageAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(new BrowseResult(
                sessionId, "", BrowseStatus.SessionNotFound, null, null, 0, false, null, null, null, null));

        public Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default)
        {
            SnapshotSessionIds.Add(request.SessionId);
            return Task.FromResult(new SnapshotResult(
                request.SessionId, "https://example.com", "- heading \"Example\"", 1, null));
        }

        public Task<WebActionResult> ActionAsync(WebActionRequest request, CancellationToken ct = default)
        {
            ActionSessionIds.Add(request.SessionId);
            return Task.FromResult(new WebActionResult(
                request.SessionId, WebActionStatus.Success, "https://example.com", false, null, null, null));
        }

        public Task<ImageFetchResult> FetchImagesAsync(
            ImageFetchRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ImageFetchResult(request.SessionId, []));

        public Task CloseSessionAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    }
}