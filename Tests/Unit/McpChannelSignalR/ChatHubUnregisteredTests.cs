using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Modules;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

// Reaching the hub is not being somebody. A connection that never registered is refused on every
// call but registration itself, so it cannot read a transcript, change a topic or answer an
// approval it was never shown. Asked over a real connection to a hub registered the way the server
// registers it, because the refusal is the hub pipeline's and a call on a hub instance skips it.
public sealed class ChatHubUnregisteredTests : IAsyncLifetime
{
    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly Mock<IThreadStateStore> _store = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private WebApplication _app = null!;
    private HubConnection _connection = null!;

    public ChatHubUnregisteredTests()
    {
        _streamService = new StreamService(
            _sessionService,
            new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);
    }

    public async Task InitializeAsync()
    {
        var attachmentSettings = new AttachmentSettings { StoragePath = _root };
        var time = new FakeTimeProvider();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddChatSignalR()
            .AddSingleton(_sessionService)
            .AddSingleton(_streamService)
            .AddSingleton(new ApprovalService(
                _streamService,
                _sessionService,
                new Mock<IHubNotificationSender>().Object,
                NullLogger<ApprovalService>.Instance))
            .AddSingleton(new ChannelNotificationEmitter(
                new ChannelInbox(new FakeTimeProvider()), DeliveryPolicy.Broadcast))
            .AddSingleton(new Mock<IAgentCatalog>().Object)
            .AddSingleton(_store.Object)
            .AddSingleton(new Mock<IPushSubscriptionStore>().Object)
            .AddSingleton(new AttachmentService(
                attachmentSettings,
                new AttachmentTickets(attachmentSettings, time),
                new AttachmentStore(attachmentSettings, new RetentionSettings(), new FakeConversationLiveness(), time, NullLogger<AttachmentStore>.Instance),
                NullLogger<AttachmentService>.Instance))
            .AddSingleton(new Mock<IHubNotificationSender>().Object)
            .AddSingleton(new RetentionSettings())
            .AddSingleton(new DictationSettings());

        _app = builder.Build();
        _app.MapHub<ChatHub>("/hubs/chat");
        await _app.StartAsync();

        var server = _app.GetTestServer();
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        await _connection.StartAsync();
    }

    // Every argument is passed: a hub method's defaults are the server's, and a call that leaves one
    // out is refused for its shape before the guard is ever asked.
    public static TheoryData<string, object?[]> Calls => new()
    {
        { "GetHistory", ["jack", 7L, 42L] },
        { "GetTopicPage", ["jack", "default", null, null, false] },
        { "SearchTopics", ["jack", "plans", "default", null, null] },
        { "SaveTopic", [new TopicMetadata("topic-1", 7, 42, "jack", "Chat", DateTimeOffset.UnixEpoch, null), false] },
        { "DeleteTopic", ["jack", "topic-1", 7L, 42L] },
        { "CancelTopic", ["topic-1"] },
        { "StartSession", ["jack", "topic-1", 7L, 42L, null] },
        { "RespondToApprovalAsync", ["approval-1", ToolApprovalResult.Approved] },
        { "GetPendingApprovalForTopic", ["topic-1"] },
        { "IsApprovalPending", ["approval-1"] },
        { "MarkTopicRead", ["jack", 7L, "topic-1"] },
        { "IsProcessing", ["topic-1"] },
        { "GetStreamState", ["topic-1"] },
        { "JoinSpace", ["default"] },
        { "GetAgents", [] },
        { "ValidateAgent", ["jack"] },
        { "GetAttachmentLimits", [] },
        { "CreateDictationTicket", [] },
        { "CreateUploadTicket", ["topic-1"] },
        { "CreateAttachmentDownload", ["attachment-1"] },
        { "SubscribePush", [new PushSubscriptionDto("https://push.example/1", "key", "auth")] },
        { "ReplacePushSubscription", [new PushSubscriptionDto("https://push.example/2", "key", "auth"), "https://push.example/1"] },
        { "UnsubscribePush", ["https://push.example/1"] },
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task AnUnregisteredConnection_IsRefused(string method, object?[] args)
    {
        var refusal = await Should.ThrowAsync<HubException>(
            () => _connection.InvokeCoreAsync(method, typeof(object), args), method);

        refusal.Message.ShouldContain("not registered");

        // Refused before anything is read or written, not after.
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AConnectionThatRegisters_IsLetThrough()
    {
        _store.Setup(s => s.GetHistoryAsync("jack", 7, 42)).ReturnsAsync([]);

        await _connection.InvokeAsync("RegisterUser", "fran");
        var history = await _connection.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>("GetHistory", "jack", 7L, 42L);

        history.ShouldBeEmpty();
    }

    // The liveness probe reads nothing, and a client asks it of a connection it has just come
    // back to, before it has had the chance to register again.
    [Fact]
    public async Task ThePingFromAnUnregisteredConnection_IsAnswered()
    {
        (await _connection.InvokeAsync<bool>("Ping")).ShouldBeTrue();
    }

    // A stream answers the way a turn's error does: one error chunk that ends it. Here the topic is
    // mid-reply, so a stream that let the caller in would have something to show.
    [Fact]
    public async Task AResumedStreamFromAnUnregisteredConnection_EndsWithTheRefusal()
    {
        _sessionService.StartSession("topic-1", "jack", 7, 42, null);
        _streamService.GetOrCreateStream("topic-1", "hello", "fran", CancellationToken.None);

        var chunks = await ReadAllAsync(_connection.StreamAsync<ChatStreamMessage>("ResumeStream", "topic-1"));

        ShouldBeTheRefusal(chunks);
    }

    [Fact]
    public async Task AMessageSentFromAnUnregisteredConnection_EndsWithTheRefusal()
    {
        _sessionService.StartSession("topic-1", "jack", 7, 42, null);

        var chunks = await ReadAllAsync(_connection.StreamAsync<ChatStreamMessage>(
            "SendMessage", "topic-1", "hello", null, null, null));

        ShouldBeTheRefusal(chunks);
        _streamService.IsStreaming("topic-1").ShouldBeFalse();
    }

    // False is the client's signal that there is no stream to enqueue onto, so it sends on a stream
    // of its own instead — which is where the refusal is then said.
    [Fact]
    public async Task AMessageEnqueuedFromAnUnregisteredConnection_IsNotTaken()
    {
        _sessionService.StartSession("topic-1", "jack", 7, 42, null);
        _streamService.GetOrCreateStream("topic-1", "hello", "fran", CancellationToken.None);

        var enqueued = await _connection.InvokeAsync<bool>(
            "EnqueueMessage", "topic-1", "again", null, null, null);

        enqueued.ShouldBeFalse();
    }

    // Bounded, so a stream that let the caller in fails here instead of waiting for a reply.
    private static async Task<List<ChatStreamMessage>> ReadAllAsync(IAsyncEnumerable<ChatStreamMessage> stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await stream.ToListAsync(timeout.Token);
    }

    private static void ShouldBeTheRefusal(List<ChatStreamMessage> chunks)
    {
        var chunk = chunks.ShouldHaveSingleItem();
        chunk.Error.ShouldNotBeNull().ShouldContain("not registered");
        chunk.IsComplete.ShouldBeTrue();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _app.DisposeAsync();
        _streamService.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}