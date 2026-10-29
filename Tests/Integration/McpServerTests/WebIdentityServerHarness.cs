using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Tools.Web;
using McpServerWebSearch.Handoff;
using McpServerWebSearch.Modules;
using McpServerWebSearch.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServers;

namespace Tests.Integration.McpServerTests;

// The browse server as the agent sees it, with a browser that only records: a real host, a real
// MCP client sending the conversation context in `_meta`, and the identity map the test names.
internal sealed class WebIdentityServerHarness : IAsyncDisposable
{
    public static readonly PersonIdentity Fran =
        new() { Name = "fran", Telegram = "fran_tg", WebChat = "Fran", Voice = "fran" };

    public static readonly PersonIdentity Ana = new() { Name = "ana", Telegram = "ana_tg", WebChat = "Ana" };

    // Enrolled on voice and nowhere else: there is no WebChat to put a voice handoff's link in.
    public static readonly PersonIdentity Mo = new() { Name = "mo", Voice = "mo" };

    public static readonly IReadOnlyList<string> ShippedVerifiedChannels =
        [IdentityMap.TelegramChannelId, IdentityMap.VoiceChannelId];

    // Once a real login stands behind WebChat, it joins the list — and a voice login's link can go
    // there.
    public static readonly IReadOnlyList<string> WebChatVerified =
        [.. ShippedVerifiedChannels, IdentityMap.WebChatChannelId];

    private readonly WebApplication _app;
    private readonly string _endpoint;

    public RecordingBrowser Browser { get; }

    public string BaseAddress { get; }

    private WebIdentityServerHarness(WebApplication app, string baseAddress, RecordingBrowser browser)
    {
        _app = app;
        BaseAddress = baseAddress;
        _endpoint = $"{baseAddress}/mcp";
        Browser = browser;
    }

    public IServiceProvider Services => _app.Services;

    public static async Task<WebIdentityServerHarness> StartAsync(
        IReadOnlyList<PersonIdentity>? people = null,
        Action<IServiceCollection>? configure = null,
        Func<McpSettings, McpSettings>? settings = null)
    {
        var browser = new RecordingBrowser();
        var port = TestPort.GetAvailable();
        var baseAddress = $"http://localhost:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        var configured = new McpSettings
        {
            BraveSearch = new BraveSearchConfiguration { ApiKey = "test" },
            Camoufox = null,
            CapSolver = null,
            RedisConnectionString = McpServerRegistrations.UnreachableRedis,
            Mcp = McpTestSecret.Gate,
            // The shipped list: WebChat is not verified, so a test about a WebChat turn or a voice
            // login's WebChat delivery says so in its own settings.
            Identity = new IdentityConfiguration
            {
                People = people ?? [Fran, Ana, Mo],
                VerifiedChannels = ShippedVerifiedChannels
            }
        };
        builder.Services.ConfigureMcp(settings?.Invoke(configured) ?? configured);
        builder.Services.RemoveAll<IWebBrowser>();
        builder.Services.AddSingleton<IWebBrowser>(browser);
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseWebSockets();
        app.MapMcp("/mcp");
        app.MapHandoff();
        await app.StartAsync();
        return new WebIdentityServerHarness(app, baseAddress, browser);
    }

    public async Task<CallToolResult> CallAsync(
        string tool, Dictionary<string, object?> arguments, ConversationContext? context)
    {
        await using var client = await McpClient.CreateAsync(McpTestSecret.Transport(_endpoint));
        var found = (await client.ListToolsAsync()).Single(t => t.Name == tool);
        return context is null
            ? await found.CallAsync(arguments)
            : await found.WithMeta(MetaFor(context)).CallAsync(arguments);
    }

    public async Task<IReadOnlyList<string>> ToolNamesAsync()
    {
        await using var client = await McpClient.CreateAsync(McpTestSecret.Transport(_endpoint));
        return (await client.ListToolsAsync()).Select(t => t.Name).ToList();
    }

    public static ConversationContext Context(
        string userId, string conversationId = "conv-1", string channelId = "telegram",
        MessageOriginKind? originKind = null, string? address = null) =>
        new("jonas", conversationId, userId, new ReplyTarget(channelId, conversationId, address),
            OriginKind: originKind);

    public static JsonObject MetaFor(ConversationContext context) => new()
    {
        [ChannelProtocol.ConversationContextMetaKey] =
            JsonSerializer.SerializeToNode(context, ChannelProtocol.SerializerOptions)
    };

    public static JsonObject Envelope(CallToolResult result) => JsonNode.Parse(result.Content
        .OfType<TextContentBlock>()
        .Select(t => t.Text)
        .First()
        .Split("\n\n")[0])!.AsObject();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    internal sealed class RecordingBrowser : IWebBrowser
    {
        public ConcurrentQueue<BrowseRequest> Browses { get; } = [];
        public ConcurrentQueue<SnapshotRequest> Snapshots { get; } = [];

        public Task<BrowseResult> NavigateAsync(BrowseRequest request, CancellationToken ct = default)
        {
            Browses.Enqueue(request);
            return Task.FromResult(new BrowseResult(
                request.SessionId, request.Url, BrowseStatus.Success,
                "Example", "content", 7, false, null, null, null, null));
        }

        public Task<BrowseResult> GetCurrentPageAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(new BrowseResult(
                sessionId, "", BrowseStatus.SessionNotFound, null, null, 0, false, null, null, null, null));

        public Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default)
        {
            Snapshots.Enqueue(request);
            return Task.FromResult(new SnapshotResult(
                request.SessionId, "https://example.com", "- heading \"Example\"", 1, null));
        }

        public Task<WebActionResult> ActionAsync(WebActionRequest request, CancellationToken ct = default)
            => Task.FromResult(new WebActionResult(
                request.SessionId, WebActionStatus.Success, "https://example.com", false, null, null, null));

        public Task<ImageFetchResult> FetchImagesAsync(
            ImageFetchRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ImageFetchResult(request.SessionId, []));

        public Task CloseSessionAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    }
}