using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
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

// The browse server as the agent sees it, with a browser that only records: a real host, and a real
// MCP client sending the conversation context in `_meta`, whose principal says whose account a turn
// acts for.
internal sealed class WebIdentityServerHarness : IAsyncDisposable
{
    private const string Household = "4f1c9a7e2b6d4e0a9c3b8d5e7f1a2c4b";

    // People of one household, as the accounts service names them: opaque ids, never names.
    public static readonly Account Fran = new(Household, "a3f09c1d7e2b4c6a8d5e0f1b2c3d4e5f");

    public static readonly Account Ana = new(Household, "b7e1d2c3a4f5061728394a5b6c7d8e9f");

    private readonly WebApplication _app;
    private readonly string _endpoint;

    public RecordingBrowser Browser { get; }

    // The listener the agent dials, and the one the proxy routes /browse/* to: a person's browser
    // only ever reaches the second.
    public string McpAddress { get; }

    public string PagesAddress { get; }

    private WebIdentityServerHarness(
        WebApplication app, string mcpAddress, string pagesAddress, RecordingBrowser browser)
    {
        _app = app;
        McpAddress = mcpAddress;
        PagesAddress = pagesAddress;
        _endpoint = $"{mcpAddress}/mcp";
        Browser = browser;
    }

    public IServiceProvider Services => _app.Services;

    public static async Task<WebIdentityServerHarness> StartAsync(
        Action<IServiceCollection>? configure = null,
        Func<McpSettings, McpSettings>? settings = null,
        bool loggedIn = true)
    {
        var browser = new RecordingBrowser();
        var listeners = new ListenerConfiguration { Mcp = TestPort.GetAvailable(), Pages = TestPort.GetAvailable() };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, listeners.Mcp);
            options.Listen(IPAddress.Loopback, listeners.Pages);
        });
        var configured = new McpSettings
        {
            BraveSearch = new BraveSearchConfiguration { ApiKey = "test" },
            Camoufox = null,
            CapSolver = null,
            RedisConnectionString = McpServerRegistrations.UnreachableRedis,
            Mcp = McpTestSecret.Gate,
            Listeners = listeners
        };
        builder.Services.ConfigureMcp(settings?.Invoke(configured) ?? configured);
        builder.Services.RemoveAll<IWebBrowser>();
        builder.Services.AddSingleton<IWebBrowser>(browser);
        // Whether the person holds a login on whatever site an asMe browse names: the real answer
        // reads a store this host has no Redis for, and what an asMe browse does with it is the
        // subject here, not how logins are kept.
        builder.Services.RemoveAll<ILoginSites>();
        builder.Services.AddSingleton<ILoginSites>(new AnsweringLoginSites(loggedIn));
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseWebSockets();
        app.MapMcp("/mcp");
        app.MapHandoff();
        await app.StartAsync();
        return new WebIdentityServerHarness(
            app, $"http://localhost:{listeners.Mcp}", $"http://localhost:{listeners.Pages}", browser);
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

    // A turn as its channel stamps it: the channel's own name for the person, and the account the
    // channel resolved them to — none for the voice guest or a principal from before accounts.
    public static ConversationContext Context(
        string userId, Account? account, string conversationId = "conv-1", string channelId = "telegram",
        MessageOriginKind? originKind = null, string? address = null) =>
        new("jonas", conversationId, userId, new ReplyTarget(channelId, conversationId, address),
            OriginKind: originKind, Principal: new Principal(channelId, userId, account));

    // Each channel's own name for a person: Telegram's is the username, the chat client's and a
    // recognised voice speaker's are the account's id.
    public static ConversationContext FromTelegram(Account account, string username = "fran_tg", string conversationId = "conv-1") =>
        Context(username, account, conversationId);

    public static ConversationContext FromChatClient(Account account, string conversationId = "conv-1") =>
        Context(account.UserId, account, conversationId, channelId: "signalr");

    public static ConversationContext FromVoice(Account account, string conversationId = "voice-9", string address = "kitchen-01") =>
        Context(account.UserId, account, conversationId, channelId: "voice", address: address);

    // The satellite heard nobody it knows: the voice channel's guest, with no account.
    public static ConversationContext FromVoiceGuest(string conversationId = "voice-9") =>
        Context("household", null, conversationId, channelId: "voice", address: "kitchen-01");

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

    private sealed class AnsweringLoginSites(bool loggedIn) : ILoginSites
    {
        public Task<bool> HasLoginAsync(string person, string site, CancellationToken ct = default) =>
            Task.FromResult(loggedIn);
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