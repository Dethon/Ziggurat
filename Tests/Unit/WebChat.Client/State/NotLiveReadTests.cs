using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// A read that feeds a store and comes back not live leaves the store holding what it already
// had. Nothing further is needed to recover: the connection epoch reloads on becoming live.
// None of these raises a toast — the user asked for none of them.
public sealed class NotLiveReadTests
{
    private static readonly AgentCatalogEntry _agentOne = new("agent-1", "Agent One", null);
    private static readonly AgentCatalogEntry _agentTwo = new("agent-2", "Agent Two", null);

    [Fact]
    public async Task AnAgentSwitch_WhileNotLive_LeavesTheConversationListAlone()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        await SelectFirstAgentAsync(client);
        client.Dispatcher.Dispatch(new TopicsLoaded([StoredTopicOne()]));

        client.GoNotLive();
        await client.Service<AgentSelectionEffect>().HandleAgentChangedAsync("agent-2");

        client.Topics.State.Topics.Select(topic => topic.TopicId).ShouldBe(["topic-1"]);
    }

    [Fact]
    public async Task AnAgentSwitch_WhileNotLive_RaisesNoToast()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        await SelectFirstAgentAsync(client);

        client.GoNotLive();
        await client.Service<AgentSelectionEffect>().HandleAgentChangedAsync("agent-2");

        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAgentSwitch_WhileLive_StillReplacesTheConversationListWithTheServersAnswer()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        await SelectFirstAgentAsync(client);
        client.Dispatcher.Dispatch(new TopicsLoaded([StoredTopicOne()]));
        transport.Answer("GetTopicPage", new TopicPage([TestChat.Topic("topic-2", 11, 21, "agent-2")], null));

        await client.Service<AgentSelectionEffect>().HandleAgentChangedAsync("agent-2");

        client.Topics.State.Topics.Select(topic => topic.TopicId).ShouldBe(["topic-2"]);
    }

    [Fact]
    public async Task AnAgentSwitch_WhileLive_StillEmptiesTheListOnAGenuinelyEmptyAnswer()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        await SelectFirstAgentAsync(client);
        client.Dispatcher.Dispatch(new TopicsLoaded([StoredTopicOne()]));

        await client.Service<AgentSelectionEffect>().HandleAgentChangedAsync("agent-2");

        client.Topics.State.Topics.ShouldBeEmpty();
    }

    // Opening a conversation is now the only thing that fetches a transcript, so it is where a
    // history read that could not be made has to leave what is already on screen alone.
    [Fact]
    public async Task AHistoryFetch_ThatCouldNotBeMade_LeavesTheTranscriptOnScreen()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        await SelectFirstAgentAsync(client);
        client.Dispatcher.Dispatch(new TopicsLoaded([StoredTopicOne()]));
        client.Dispatcher.Dispatch(new MessagesLoaded("topic-1", [Message("m-1", "still here")]));
        transport.Answer("GetHistory", _ =>
        {
            transport.State = HubConnectionState.Reconnecting;
            return (IReadOnlyList<ChatHistoryMessage>)[TestChat.HistoryMessage("m-2", "fresh")];
        });

        await client.Service<TopicSelectionEffect>().HandleSelectTopicAsync("topic-1");

        client.Messages.State.MessagesByTopic["topic-1"].Single().Content.ShouldBe("still here");
        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAgentSwitch_WhileLive_ReadsNoHistoryAtAll()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        await SelectFirstAgentAsync(client);
        transport.Answer("GetTopicPage", new TopicPage([TestChat.Topic("topic-2", 11, 21, "agent-2")], null));
        transport.Answer("GetHistory", (IReadOnlyList<ChatHistoryMessage>)[TestChat.HistoryMessage("m-2", "fresh")]);

        await client.Service<AgentSelectionEffect>().HandleAgentChangedAsync("agent-2");

        client.Topics.State.Topics.Select(topic => topic.TopicId).ShouldBe(["topic-2"]);
        client.Messages.State.MessagesByTopic.ShouldBeEmpty();
    }

    // The first selection belongs to first load, so the effect only reloads from the second
    // one on. Priming it is what makes the switch under test a switch.
    private static async Task SelectFirstAgentAsync(ScriptedChatClient client)
    {
        client.Dispatcher.Dispatch(new SetAgents([_agentOne, _agentTwo]));
        await client.Service<AgentSelectionEffect>().HandleAgentChangedAsync("agent-1");
    }

    private static StoredTopic StoredTopicOne() => StoredTopic.FromMetadata(TestChat.Topic("topic-1"));

    private static ChatMessageModel Message(string messageId, string content) =>
        TestChat.HistoryMessage(messageId, content).ToChatMessageModel();
}