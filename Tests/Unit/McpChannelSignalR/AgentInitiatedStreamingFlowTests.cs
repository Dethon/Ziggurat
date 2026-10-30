using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using McpChannelSignalR.McpTools;
using McpChannelSignalR.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class AgentInitiatedStreamingFlowTests
{
    [Fact]
    public async Task AttachThenSendReply_DeliversChunksAndCompletionToLiveSubscriber()
    {
        // The full channel-server flow for a download alert, through the same MCP tool
        // entrypoints the agent invokes: turn-start attach, then send_reply chunks. The
        // subscriber plays the role of a browser that resumed after OnStreamStarted.
        var sessionService = new SessionService();
        var streamService = new StreamService(
            sessionService,
            new Mock<IThreadStateStore>().Object,
            new Mock<IPushNotificationService>().Object,
            new Mock<ILogger<StreamService>>().Object);
        var hubSender = new Mock<IHubNotificationSender>();
        var services = new ServiceCollection()
            .AddSingleton(sessionService)
            .AddSingleton(streamService)
            .AddSingleton<IStreamService>(streamService)
            .AddSingleton(hubSender.Object)
            .AddSingleton(new Mock<IThreadStateStore>().Object)
            .BuildServiceProvider();
        sessionService.StartSession("topic-1", "jack", 7, 42, "default", "Downloads");

        await CreateConversationTool.McpRun(
            "jack", string.Empty, "fran", services,
            initialPrompt: "[download-complete] film.mkv", address: null, existingConversationId: "7:42");

        var subscription = streamService.SubscribeToStream("topic-1", CancellationToken.None);
        subscription.ShouldNotBeNull();

        await SendReplyTool.McpRun("7:42", "Your download finished: film.mkv", ReplyContentType.Text, false, "m1", services);
        await SendReplyTool.McpRun("7:42", string.Empty, ReplyContentType.StreamComplete, true, null, services);

        var received = new List<ChatStreamMessage>();
        await foreach (var msg in subscription)
        {
            received.Add(msg);
        }

        received.ShouldContain(m => m.Content == "Your download finished: film.mkv" && m.MessageId == "m1");
        received.ShouldContain(m => m.IsComplete);
        streamService.IsStreaming("topic-1").ShouldBeFalse();
        hubSender.Verify(s => s.SendToGroupAsync(
            "space:default",
            "OnStreamStarted",
            It.Is<StreamStartedNotification>(n => n.TopicId == "topic-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}