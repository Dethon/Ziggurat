using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.WebChat;
using Domain.Monitor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// A channel that draws a reply's tool calls (the chat client) is sent each call as the model writes it
// and its answer once it ran, in the order they happened, beside the words. Any other channel is sent
// none: its server may not know these kinds of reply at all, and every reply is awaited.
public class ChatMonitorToolCallDeliveryTests
{
    [Fact]
    public async Task AChannelThatShowsToolCalls_IsSentEachCallAndItsAnswer_InOrder()
    {
        var webchat = Channel("signalr", showsToolCalls: true);

        await RunAsync(webchat);

        var kinds = webchat.SentReplies.Select(r => r.ContentType).ToList();
        kinds.ShouldBe([ReplyContentType.ToolCall, ReplyContentType.ToolResult, ReplyContentType.Text, ReplyContentType.StreamComplete]);
        var call = Step(webchat.SentReplies[0]);
        (call.CallId, call.Name).ShouldBe(("c1", "mcp__x__glob"));
        JsonDocument.Parse(call.Arguments!).RootElement.GetProperty("pattern").GetString().ShouldBe("*");
        var answer = Step(webchat.SentReplies[1]);
        (answer.CallId, answer.Result, answer.Outcome).ShouldBe(("c1", "a.txt", ToolCallOutcome.Done));
    }

    [Fact]
    public async Task AChannelThatShowsNoToolCalls_IsSentNone_AndItsFirstReplyIsItsFirstWords()
    {
        var telegram = Channel("telegram", showsToolCalls: false);

        var published = await RunAsync(telegram);

        telegram.SentReplies.Select(r => r.ContentType)
            .ShouldBe([ReplyContentType.Text, ReplyContentType.StreamComplete]);
        published.OfType<LatencyEvent>().Where(e => e.Stage == LatencyStage.FirstReply).ShouldHaveSingleItem();
    }

    private static ToolCallStep Step(SendReplyParams reply) =>
        JsonSerializer.Deserialize<ToolCallStep>(reply.Content, ChannelProtocol.SerializerOptions)!;

    private static FakeChannelConnection Channel(string id, bool showsToolCalls)
    {
        var channel = new FakeChannelConnection { ChannelId = id, ShowsToolCalls = showsToolCalls };
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
                        new AgentResponseUpdate
                        {
                            MessageId = "m1",
                            Contents = [new FunctionCallContent("c1", "mcp__x__glob", new Dictionary<string, object?> { ["pattern"] = "*" })]
                        },
                        new AgentResponseUpdate
                        {
                            MessageId = "t1", Role = ChatRole.Tool, Contents = [new FunctionResultContent("c1", "a.txt")]
                        },
                        new AgentResponseUpdate { MessageId = "m2", Contents = [new TextContent("Found a.txt.")] }
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