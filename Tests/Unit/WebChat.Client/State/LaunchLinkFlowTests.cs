using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.State;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// A finished reply's notification, tapped, opens the app at its space naming the conversation. The
// whole client, because several effects pick an agent as the app starts — first load, the space
// opening on its own agent, a catalog arriving — and the one the address names must outlast them all.
public sealed class LaunchLinkFlowTests
{
    [Fact]
    public async Task AnAddressNamingAnotherAgentsConversation_OpensOnThatAgentAndThatConversation()
    {
        await using var client = new ScriptedChatClient("http://localhost/hearth?agent=agent-2&topic=topic-9");
        var answerDefaults = client.Factory.CreateBehavior;
        client.Factory.CreateBehavior = () =>
        {
            var connection = answerDefaults();
            connection.Answer("GetAgents", (IReadOnlyList<AgentCatalogEntry>)
            [
                new AgentCatalogEntry("agent-1", "Agent One", null) { IsHouseholdDefault = true },
                new AgentCatalogEntry("agent-2", "Agent Two", null)
            ]);
            connection.Answer("GetTopicPageFiltered", (object?[] args) => (string?)args[0] == "agent-2"
                ? new TopicPage([TestChat.Topic("topic-9", 19, 29, "agent-2")], null)
                : new TopicPage([TestChat.Topic("topic-1", 11, 21, "agent-1")], null));
            return connection;
        };

        client.Dispatcher.Dispatch(new SelectSpace("hearth"));
        client.Dispatcher.Dispatch(new Initialize());

        await TestChat.Eventually(() => client.Topics.State.SelectedTopicId == "topic-9");
        await Tests.Eventually.Settle();

        client.Topics.State.SelectedAgentId.ShouldBe("agent-2");
        client.Topics.State.SelectedTopicId.ShouldBe("topic-9");
        client.Service<Microsoft.AspNetCore.Components.NavigationManager>()
            .ShouldBeOfType<FakeNavigationManager>().NavigatedTo.ShouldContain("/hearth");
    }
}