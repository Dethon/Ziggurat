using Domain.Agents;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Mcp.Hosting;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Unit.McpChannelSignalR;

// Two of the three models on offer today are text-only, so a message with a file that the model
// cannot read is the common path rather than the corner. The composer blocks it; this is the
// server's own guard, for the race where the model changed between picking and sending.
public sealed class ChatHubCapabilityRefusalTests : IDisposable
{
    private const string TopicId = "topic-1";

    private static readonly AttachmentReference _photo = new()
    {
        Id = "7-42/abc",
        FileName = "photo.png",
        MediaType = "image/png",
        SizeBytes = 4
    };

    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly ChannelInbox _inbox = new(new FakeTimeProvider());
    private readonly MutableAgentCatalog _catalog = new();
    private readonly ChatHub _hub;

    public ChatHubCapabilityRefusalTests()
    {
        _streamService = new StreamService(
            _sessionService,
            new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);

        _catalog.Replace([
            new AgentCatalogEntry(
                "jack", "Jack", null,
                DefaultModel: "text/only",
                PatchableModels: [new PatchableModel("sees/pictures", "Sees", [AttachmentKind.Image])],
                DefaultModelAttachmentKinds: [])
        ]);

        _hub = new ChatHub(
            _sessionService,
            _streamService,
            approvalService: null!,
            new ChannelNotificationEmitter(_inbox, DeliveryPolicy.Broadcast),
            _catalog,
            redisStateService: null!,
            pushSubscriptionStore: null!,
            attachmentService: null!,
            new DictationSettings(),
            NullLogger<ChatHub>.Instance)
        {
            Context = new RegisteredCaller()
        };

        _sessionService.StartSession(TopicId, "jack", 7, 42, "default", "Chat");
    }

    public void Dispose() => _streamService.Dispose();

    [Fact]
    public async Task AnIncapableModel_IsRefusedBeforeAnythingIsEmitted()
    {
        // Registered first: Broadcast discards an item with no subscriber, so without this the
        // empty batch below would be empty whether or not anything was emitted.
        await ReceivedAsync();

        var chunks = await SendAsync(configPatch: null, [_photo]);

        chunks.ShouldContain(c => !string.IsNullOrEmpty(c.Error) && c.IsComplete);
        chunks.ShouldContain(c => (c.Error ?? "").Contains("text/only"));
        (await ReceivedAsync()).ShouldBeEmpty();
    }

    // No turn was created, so no browser is shown a message that was never taken.
    [Fact]
    public async Task ARefusal_PutsNoUserMessageOnTheTopicsStream()
    {
        var chunks = await SendAsync(configPatch: null, [_photo]);

        chunks.ShouldAllBe(c => c.UserMessage == null);
    }

    [Fact]
    public async Task ARefusal_LeavesTheTopicWithNoTurnInFlight()
    {
        await SendAsync(configPatch: null, [_photo]);

        _streamService.IsStreaming(TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task ACapableModelChosenForTheMessage_IsNotRefused()
    {
        var chunks = await SendAsync(new AgentConfigPatch { Model = "sees/pictures" }, [_photo]);

        chunks.ShouldNotContain(c => (c.Error ?? "").Contains("cannot read"));
    }

    [Fact]
    public async Task AMessageWithNoAttachments_IsNeverRefused()
    {
        var chunks = await SendAsync(configPatch: null, attachments: null);

        chunks.ShouldNotContain(c => (c.Error ?? "").Contains("cannot read"));
    }

    [Fact]
    public async Task AnEnqueuedMessageWithAnIncapableModel_IsRefusedTheSameWay()
    {
        await ReceivedAsync();
        var (channel, _) = _streamService.GetOrCreateStream(TopicId, "hola", "fran", CancellationToken.None);
        var subscription = channel.Subscribe();

        (await _hub.EnqueueMessage(TopicId, "look", null, null, [_photo])).ShouldBeTrue();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<ChatStreamMessage>();
        try
        {
            await foreach (var chunk in subscription.ReadAllAsync(cts.Token))
            {
                received.Add(chunk);
            }
        }
        catch (OperationCanceledException)
        {
        }

        received.ShouldContain(c => (c.Error ?? "").Contains("text/only"));
        (await ReceivedAsync()).ShouldBeEmpty();
    }

    // A refusal that reached the emitter would leave a notification for the agent's pump to
    // find; an empty batch is the whole of "no turn was created and no agent was woken".
    private Task<IReadOnlyList<ChannelInboxItem>> ReceivedAsync() =>
        _inbox.ReceiveAsync("channel-signalr", TimeSpan.Zero, CancellationToken.None);

    private async Task<List<ChatStreamMessage>> SendAsync(
        AgentConfigPatch? configPatch, IReadOnlyList<AttachmentReference>? attachments)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var chunks = new List<ChatStreamMessage>();
        await foreach (var chunk in _hub.SendMessage(TopicId, "look", null, configPatch, attachments, cts.Token))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }
}