using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Monitor;
using Infrastructure.Metrics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;
using Xunit;

namespace Tests.Unit.Domain.Monitor;

public class ChatMonitorScheduleMetricsTests
{
    [Fact]
    public void FromMessage_WithScheduleOrigin_ReturnsEvent()
    {
        var msg = new ChannelMessage
        {
            ConversationId = "c", Content = "do the thing", Sender = "scheduler", ChannelId = "scheduling",
            AgentId = "jonas", Origin = new MessageOrigin(MessageOriginKind.Schedule, "morning-news")
        };

        var evt = ScheduleExecutionEvent.FromMessage(msg, durationMs: 1234, success: true, error: null);

        evt.ShouldNotBeNull();
        evt.ScheduleId.ShouldBe("morning-news");
        evt.AgentId.ShouldBe("jonas");
        evt.Prompt.ShouldBe("do the thing");
        evt.DurationMs.ShouldBe(1234);
        evt.Success.ShouldBeTrue();
    }

    [Fact]
    public void FromMessage_WithNonScheduleMessage_ReturnsNull()
    {
        var msg = new ChannelMessage { ConversationId = "c", Content = "hi", Sender = "u", ChannelId = "signalr" };
        ScheduleExecutionEvent.FromMessage(msg, 1, true, null).ShouldBeNull();
    }

    [Fact]
    public async Task Monitor_ScheduledMessage_OneDeliveryTargetFails_DeliversToOthersAndEmitsMetricOnce()
    {
        // The bad target is listed first so that, without per-target isolation, it would
        // abort the fan-out before the healthy target is ever reached.
        var scheduledMessage = ScheduledMessage(
            new ReplyTarget("bad", "b"), new ReplyTarget("good", "g"));
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", scheduledMessage);

        var bad = new Mock<IChannelConnection>();
        bad.SetupGet(c => c.ChannelId).Returns("bad");
        bad.SetupGet(c => c.Messages).Returns(AsyncEnumerable.Empty<ChannelMessage>());
        bad.Setup(c => c.SendReplyAsync(It.IsAny<SendReplyParams>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("channel down"));
        var good = MonitorTestMocks.CreateChannel("good");

        var published = CapturePublishedMetrics(out var metricsPublisher);

        var monitor = BuildMonitor([scheduling, bad.Object, good], metricsPublisher);
        await monitor.Monitor(CancellationToken.None);

        good.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
        published.OfType<ErrorEvent>().ShouldNotBeEmpty();
        var evt = published.OfType<ScheduleExecutionEvent>().ShouldHaveSingleItem();
        evt.Success.ShouldBeTrue();
    }

    // A behaviour change, not a refactor. The turn's token used to be handed to every publish, and
    // BufferedMetricsPublisher drops an event whose token is already cancelled — so a turn the user
    // gave up on silently lost the one event that says how long it ran. The real buffered publisher
    // is used here because that drop is the thing under test; a recording publisher would ignore
    // the token and stay green either way.
    [Fact]
    public async Task Monitor_ScheduledMessage_TurnCancelledMidRun_StillRecordsItsMetrics()
    {
        var scheduledMessage = ScheduledMessage();
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", scheduledMessage);
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var context = threadResolver.Resolve(new AgentKey("fire-1", "jonas"));

        var sink = new RecordingSink();
        var publisher = new BufferedMetricsPublisher(sink);

        // Deliver a reply chunk (so first-reply latency has something to complete on) and then
        // cancel the turn from inside it, exactly as the stop button does.
        var agent = new FakeAiAgent
        {
            UpdatesToYield = [new AgentResponseUpdate { Contents = [new TextContent("partial")] }],
            TurnGate = _ =>
            {
                context.Cts.Cancel();
                return Task.CompletedTask;
            }
        };

        var monitor = new ChatMonitor(
            [scheduling],
            MonitorTestMocks.CreateAgentFactory(agent),
            threadResolver,
            publisher,
            null,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier());

        await monitor.Monitor(CancellationToken.None);
        await publisher.DisposeAsync();

        sink.Events.OfType<ScheduleExecutionEvent>().ShouldHaveSingleItem()
            .ScheduleId.ShouldBe("morning-news");
        sink.Events.OfType<LatencyEvent>()
            .ShouldContain(e => e.Stage == LatencyStage.FirstReply);
    }

    private sealed class RecordingSink : IMetricSink
    {
        private readonly List<MetricEvent> _events = [];

        public IReadOnlyList<MetricEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public Task SendAsync(MetricEvent metricEvent, CancellationToken ct = default)
        {
            lock (_events)
            {
                _events.Add(metricEvent);
            }

            return Task.CompletedTask;
        }
    }

    // The operator's dashboard listed every household's fire prompts ("Dile a Iker, en una sola línea:
    // «Beber agua»"), while their conversations are theirs alone: a household's own schedule says its
    // id, its duration and how it went, and only the home's say what they asked.
    [Theory]
    [InlineData(null, "do the thing")]
    [InlineData("casa", "")]
    public async Task Monitor_ScheduledMessage_RecordsItsPromptForTheHomeAlone(string? household, string recorded)
    {
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", ScheduledMessage() with { HouseholdId = household });
        var published = CapturePublishedMetrics(out var metricsPublisher);

        var monitor = new ChatMonitor(
            ConnectionSet.Of([scheduling]),
            MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
            MonitorTestMocks.CreateThreadResolver(),
            metricsPublisher.Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier(),
            agentDefinitions: MonitorTestMocks.Households(suspended: "nobody"));
        await monitor.Monitor(CancellationToken.None);

        var evt = published.OfType<ScheduleExecutionEvent>().ShouldHaveSingleItem();
        evt.ScheduleId.ShouldBe("morning-news");
        evt.Prompt.ShouldBe(recorded);
    }

    // Casa Vidal's turn on its own Lemonade box failed with the box's 500, and its reply's delivery can
    // fail as any target can: both were filed as the deployment's own errors, so the dashboard's
    // filter for the household showed none of them and the deployment's showed another household's.
    [Fact]
    public async Task Monitor_AHouseholdsTurn_FilesItsErrorsUnderThatHousehold()
    {
        // The fire's own channel cannot take the reply, as any channel may be down.
        var scheduling = new Mock<IChannelConnection>();
        scheduling.SetupGet(c => c.ChannelId).Returns("scheduling");
        scheduling.SetupGet(c => c.Messages)
            .Returns(new[] { ScheduledMessage() with { HouseholdId = "casa" } }.ToAsyncEnumerable());
        scheduling.Setup(c => c.SendReplyAsync(It.IsAny<SendReplyParams>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("channel down"));
        var agent = new FakeAiAgent { ExceptionToThrow = new InvalidOperationException("the box answered 500") };
        var published = CapturePublishedMetrics(out var metricsPublisher);

        var monitor = new ChatMonitor(
            ConnectionSet.Of([scheduling.Object]),
            MonitorTestMocks.CreateAgentFactory(agent),
            MonitorTestMocks.CreateThreadResolver(),
            metricsPublisher.Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier(),
            agentDefinitions: MonitorTestMocks.Households(suspended: "nobody"));
        await monitor.Monitor(CancellationToken.None);

        var errors = published.OfType<ErrorEvent>().ToList();
        errors.ShouldContain(e => e.ErrorType == nameof(InvalidOperationException), string.Join("; ", published.Select(e => $"{e.GetType().Name}:{(e as ErrorEvent)?.ErrorType}:{e.TenantId}")));
        errors.ShouldContain(e => e.ErrorType == nameof(HttpRequestException));
        errors.ShouldAllBe(e => e.TenantId == "casa");
    }

    private static ChannelMessage ScheduledMessage(params ReplyTarget[] replyTo) => new()
    {
        ConversationId = "fire-1",
        Content = "do the thing",
        Sender = "scheduler",
        ChannelId = "scheduling",
        AgentId = "jonas",
        Origin = new MessageOrigin(MessageOriginKind.Schedule, "morning-news"),
        ReplyTo = replyTo.Length > 0 ? replyTo : null
    };

    private static List<MetricEvent> CapturePublishedMetrics(out Mock<IMetricsPublisher> metricsPublisher)
    {
        var published = new List<MetricEvent>();
        metricsPublisher = new Mock<IMetricsPublisher>();
        metricsPublisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(published.Add);
        return published;
    }

    private static ChatMonitor BuildMonitor(
        IReadOnlyList<IChannelConnection> channels, Mock<IMetricsPublisher> metricsPublisher) => new(
        ConnectionSet.Of(channels),
        MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
        MonitorTestMocks.CreateThreadResolver(),
        metricsPublisher.Object,
        null,
        new Mock<ILogger<ChatMonitor>>().Object,
        TestAssertions.Verifier());
}