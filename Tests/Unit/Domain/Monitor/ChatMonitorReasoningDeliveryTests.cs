using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Monitor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;

namespace Tests.Unit.Domain.Monitor;

// Every update is delivered to every target and awaited before the next is pulled, so a reasoning
// delta sent to a channel that only drops it (voice speaks no reasoning) is a round trip the answer
// waits behind. A channel declaring it shows none is sent none.
public class ChatMonitorReasoningDeliveryTests
{
    [Fact]
    public async Task AChannelThatShowsNoReasoning_IsSentTheAnswerWithoutIt()
    {
        var voice = Channel("voice", showsReasoning: false);

        var published = await RunAsync(voice);

        voice.SentReplies.ShouldNotContain(r => r.ContentType == ReplyContentType.Reasoning);
        voice.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.Text && r.Content == "Hecho.");
        voice.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete);
        // Its first reply is then the first words it can say, not a thought it never heard.
        published.OfType<LatencyEvent>().Where(e => e.Stage == LatencyStage.FirstReply).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AChannelThatShowsReasoning_IsStillSentIt()
    {
        var webchat = Channel("webchat", showsReasoning: true);

        await RunAsync(webchat);

        webchat.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.Reasoning);
    }

    private static FakeChannelConnection Channel(string id, bool showsReasoning)
    {
        var channel = new FakeChannelConnection { ChannelId = id, ShowsReasoning = showsReasoning };
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(
            conversationId: "42:13", channelId: id, agentId: "nabu"));
        channel.Complete();
        return channel;
    }

    private static async Task<List<MetricEvent>> RunAsync(IChannelConnection channel)
    {
        var published = new List<MetricEvent>();
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback((MetricEvent e) => { lock (published) { published.Add(e); } });

        await new ChatMonitor(
                ConnectionSet.Of([channel]),
                MonitorTestMocks.CreateAgentFactory(new FakeAiAgent
                {
                    UpdatesToYield =
                    [
                        new AgentResponseUpdate { Contents = [new TextReasoningContent("Simple question.")] },
                        new AgentResponseUpdate { Contents = [new TextContent("Hecho.")] }
                    ]
                }),
                MonitorTestMocks.CreateThreadResolver(),
                metrics.Object,
                null,
                new Mock<ILogger<ChatMonitor>>().Object,
                TestAssertions.Verifier())
            .Monitor(CancellationToken.None);
        return published;
    }
}