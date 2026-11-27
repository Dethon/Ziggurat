using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
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

    // A picture a tool showed the model is copied to the chat, which keeps it beside the conversation, and
    // the answer names that copy, so the page can show what the model saw.
    [Fact]
    public async Task AnAnswerThatShowedAPicture_CopiesItToTheChat_AndNamesTheCopy()
    {
        var webchat = Channel("signalr", showsToolCalls: true);
        var images = new PicturesStore();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        await images.PutAsync("42:13", "c1", new ReadImage { VirtualPath = "/vault/cat.png", MediaType = "image/png", Bytes = png }, default);
        var envelope = JsonSerializer.SerializeToElement(new FsImageReadResult
        {
            FilePath = "/vault/cat.png", MediaType = "image/png", SizeBytes = 4, Shown = true
        }, FsResultContract.SerializerOptions);

        await RunAsync(webchat,
        [
            new AgentResponseUpdate
            {
                MessageId = "m1", Contents = [new FunctionCallContent("c1", "file_read", new Dictionary<string, object?> { ["path"] = "/vault/cat.png" })]
            },
            new AgentResponseUpdate { MessageId = "t1", Role = ChatRole.Tool, Contents = [new FunctionResultContent("c1", envelope)] },
            new AgentResponseUpdate { MessageId = "m2", Contents = [new TextContent("A cat.")] }
        ], images);

        var kept = webchat.StoredAttachments.ShouldHaveSingleItem();
        (kept.ConversationId, kept.Attachment.Id, kept.Attachment.MediaType).ShouldBe(("42:13", "42-13/tool-c1", "image/png"));
        kept.Bytes.ShouldBe(png);
        Step(webchat.SentReplies.Single(r => r.ContentType == ReplyContentType.ToolResult))
            .Images.ShouldHaveSingleItem().Id.ShouldBe("42-13/tool-c1");
    }

    private sealed class PicturesStore : IReadImageStore
    {
        private readonly Dictionary<string, ReadImage> _images = [];

        public Task PutAsync(string conversationId, string callId, ReadImage image, CancellationToken ct)
        {
            _images[$"{conversationId}|{callId}"] = image;
            return Task.CompletedTask;
        }

        public Task<ReadImage?> GetAsync(string conversationId, string callId, CancellationToken ct) =>
            Task.FromResult(_images.GetValueOrDefault($"{conversationId}|{callId}"));

        public Task DeleteAsync(string conversationId, string callId, CancellationToken ct) => Task.CompletedTask;
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

    private static Task<List<MetricEvent>> RunAsync(IChannelConnection channel) => RunAsync(channel,
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
    ]);

    private static async Task<List<MetricEvent>> RunAsync(
        IChannelConnection channel, IReadOnlyList<AgentResponseUpdate> updates, IReadImageStore? images = null)
    {
        var published = new List<MetricEvent>();
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback((MetricEvent e) => { lock (published) { published.Add(e); } });

        await new ChatMonitor(
                ConnectionSet.Of([channel]),
                MonitorTestMocks.CreateAgentFactory(new FakeAiAgent { UpdatesToYield = [.. updates] }),
                MonitorTestMocks.CreateThreadResolver(),
                metrics.Object,
                null,
                new Mock<ILogger<ChatMonitor>>().Object,
                TestAssertions.Verifier(),
                readImages: images)
            .Monitor(CancellationToken.None);
        return published;
    }
}