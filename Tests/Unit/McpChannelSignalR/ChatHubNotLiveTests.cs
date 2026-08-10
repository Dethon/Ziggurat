using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Mcp.Hosting;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.McpChannelSignalR.Fixtures;

namespace Tests.Unit.McpChannelSignalR;

// A hub call answers or says it was not live (ADR 0004). Emitting reports whether anyone was
// listening, and on this side "nobody" means the agent's pump is gone — a channel server that
// restarted, or an agent in reconnect backoff. Discarding that answer left the browser streaming
// a reply that could never arrive, with nothing in the log either.
public class ChatHubNotLiveTests
{
    private const string TopicId = "topic-1";

    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly ChannelInbox _inbox = new(new FakeTimeProvider());
    private readonly ChatHub _hub;

    public ChatHubNotLiveTests()
    {
        _streamService = new StreamService(
            _sessionService,
            new Mock<IPushNotificationService>().Object,
            NullLogger<StreamService>.Instance);

        // Nobody ever polls this inbox, so every emit reports "not live" — the cold-restart shape.
        _hub = new ChatHub(
            _sessionService,
            _streamService,
            approvalService: null!,
            new ChannelNotificationEmitter(_inbox, DeliveryPolicy.Broadcast),
            new Mock<IAgentCatalog>().Object,
            redisStateService: null!,
            pushSubscriptionStore: null!,
            attachmentService: null!,
            NullLogger<ChatHub>.Instance)
        {
            Context = new RegisteredCaller()
        };

        _sessionService.StartSession(TopicId, "jack", 7, 42, "default", "Chat");
    }

    [Fact]
    public async Task SendMessage_TheEmitReachedNobody_EndsTheStreamWithAnError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var chunks = new List<ChatStreamMessage>();

        await foreach (var chunk in _hub.SendMessage(TopicId, "hola", null, null, null, cts.Token))
        {
            chunks.Add(chunk);
        }

        chunks.ShouldContain(chunk => !string.IsNullOrEmpty(chunk.Error) && chunk.IsComplete);
        _streamService.IsStreaming(TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task EnqueueMessage_TheEmitReachedNobody_TellsTheStreamInsteadOfLeavingItPending()
    {
        var (channel, _) = _streamService.GetOrCreateStream(TopicId, "hola", "fran", CancellationToken.None);
        var subscription = channel.Subscribe();

        // True, not false: false is the client's signal to open a *second* stream, which would send
        // the same prompt again over a channel that already took it.
        (await _hub.EnqueueMessage(TopicId, "y esto", null, null)).ShouldBeTrue();

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
            // The stream never ended: the assertion below reports it.
        }

        received.ShouldContain(chunk => !string.IsNullOrEmpty(chunk.Error) && chunk.IsComplete);
        _streamService.IsStreaming(TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task SendMessage_TheEmitReachedNobody_LogsIt()
    {
        var warnings = CapturingLoggerProvider.ForLevel(LogLevel.Warning);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(warnings));
        var hub = new ChatHub(
            _sessionService,
            _streamService,
            approvalService: null!,
            new ChannelNotificationEmitter(_inbox, DeliveryPolicy.Broadcast),
            new Mock<IAgentCatalog>().Object,
            redisStateService: null!,
            pushSubscriptionStore: null!,
            attachmentService: null!,
            factory.CreateLogger<ChatHub>())
        {
            Context = new RegisteredCaller()
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in hub.SendMessage(TopicId, "hola", null, null, null, cts.Token))
        {
        }

        warnings.Messages.ShouldContain(message => message.Contains(TopicId) || message.Contains("7:42"));
    }
}