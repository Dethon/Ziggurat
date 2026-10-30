using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// The three reads ticket 05 did not cover. Each answers or says not live, and each caller
// leaves the store as it found it. None of them is something the user asked for.
public sealed class NotLiveRemainingReadTests
{
    private static readonly AgentCatalogEntry _agentOne = new("agent-1", "Agent One", null);

    [Fact]
    public async Task AnAgentListFetch_ThatCouldNotBeMade_LeavesTheAgentPickerPopulated()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        client.Dispatcher.Dispatch(new SetAgents([_agentOne]));

        client.GoNotLive();
        await client.Service<InitializationEffect>().HandleInitializeAsync();

        client.Topics.State.Agents.Select(agent => agent.Id).ShouldBe(["agent-1"]);
        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAgentListFetch_WhileLive_StillReplacesTheAgentList()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        client.Dispatcher.Dispatch(new SetAgents([_agentOne]));
        transport.Answer("GetAgents", (IReadOnlyList<AgentCatalogEntry>)
            [new AgentCatalogEntry("agent-2", "Agent Two", null)]);

        await client.Service<InitializationEffect>().HandleInitializeAsync();

        client.Topics.State.Agents.Select(agent => agent.Id).ShouldBe(["agent-2"]);
    }

    [Fact]
    public async Task AStreamResume_ThatCannotAskForTheStreamState_LeavesTheStreamingStateAlone()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        var topic = StoredTopic.FromMetadata(TestChat.Topic("topic-1"));

        client.GoNotLive();
        await client.Service<IStreamResumeService>().TryResumeStreamAsync(topic);

        client.Streaming.State.StreamingTopics.ShouldBeEmpty();
        client.Service<TopicStreams>().Snapshot("topic-1").HasStream.ShouldBeFalse();
        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task AStreamResume_WhenTheServerAnswersNoStream_AlsoLeavesTheStreamingStateAlone()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        var topic = StoredTopic.FromMetadata(TestChat.Topic("topic-1"));

        await client.Service<IStreamResumeService>().TryResumeStreamAsync(topic);

        client.Streaming.State.StreamingTopics.ShouldBeEmpty();
    }

    // The other side of the same coin: a server that does have a stream still gets resumed,
    // so "could not ask" is genuinely a third outcome rather than a renamed "no stream".
    [Fact]
    public async Task AStreamResume_WhenTheServerHasAStream_StillStartsIt()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        var topic = StoredTopic.FromMetadata(TestChat.Topic("topic-1"));
        client.Dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        transport.Answer("GetStreamState", new StreamState(true, [], "m-1", null, null));
        var stream = new GatedChatStream();
        transport.Answer("ResumeStream", _ => stream.Chunks());

        await client.Service<IStreamResumeService>().TryResumeStreamAsync(topic);

        client.Streaming.State.StreamingTopics.ShouldContain("topic-1");
        stream.Release();
    }

    // The reply is shown before the wire that carries the rest of it is open, so the transport
    // can die in between. What was shown is real — the server said the reply had written it —
    // so it is kept as a message instead of being taken back off the screen.
    [Fact]
    public async Task AStreamResume_ThatShowedTheReplyAndThenLostTheTransport_KeepsWhatItShowed()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        var topic = StoredTopic.FromMetadata(TestChat.Topic("topic-1"));
        client.Dispatcher.Dispatch(new MessagesLoaded("topic-1", []));

        // The connection drops after the stream state comes back, so attaching to the stream
        // is the call that cannot be made.
        transport.Answer("GetStreamState", _ =>
        {
            transport.State = HubConnectionState.Reconnecting;
            return new StreamState(
                true, [new ChatStreamMessage { Content = "half written", MessageId = "m-1" }], "m-1", null, null);
        });

        await client.Service<IStreamResumeService>().TryResumeStreamAsync(topic);

        client.Messages.State.MessagesByTopic["topic-1"]
            .ShouldContain(message => message.Content == "half written");
        client.Streaming.State.StreamingTopics.ShouldBeEmpty();
        client.Service<TopicStreams>().Snapshot("topic-1").HasStream.ShouldBeFalse();
        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task APendingApprovalRead_ThatCouldNotBeMade_LeavesThePromptOnScreen()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        var topic = StoredTopic.FromMetadata(TestChat.Topic("topic-1"));
        client.Dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        client.Dispatcher.Dispatch(new ShowApproval("topic-1", Approval("approval-1")));

        // The connection drops after the stream state comes back, so the approval read is the
        // call that cannot be made.
        transport.Answer("GetStreamState", _ =>
        {
            transport.State = HubConnectionState.Reconnecting;
            return new StreamState(true, [], "m-1", null, null);
        });

        await client.Service<IStreamResumeService>().TryResumeStreamAsync(topic);

        client.Approvals.State.CurrentRequest?.ApprovalId.ShouldBe("approval-1");
        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    private static ToolApprovalRequestMessage Approval(string approvalId) => new(approvalId, []);
}