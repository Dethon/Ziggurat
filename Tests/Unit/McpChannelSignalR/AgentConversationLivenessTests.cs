using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using McpChannelSignalR.Attachments;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

// Whether the conversation a file was kept for still exists, asked of the history under the agent the
// file was kept for. The agent registers the home's agents with the channel, which is no list of a
// household's: a household's own agent goes by an id the home need not have, and asking only the
// home's would read its conversation as gone and sweep its files early.
public sealed class AgentConversationLivenessTests
{
    private readonly MutableAgentCatalog _home = new();
    private readonly Mock<IThreadStateStore> _store = new();
    private readonly HashSet<string> _histories = [];

    public AgentConversationLivenessTests()
    {
        _home.Replace([new AgentCatalogEntry("jonas", "Jonas", null)]);
        _store.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => _histories.Contains(key));
    }

    private AgentConversationLiveness Liveness() => new(_home, _store.Object);

    [Fact]
    public async Task AConversationHeldByAHouseholdsOwnAgent_IsAliveThoughTheHomeHasNoSuchAgent()
    {
        _histories.Add(new AgentKey("7:42", "chef").ToString());

        (await Liveness().IsAliveAsync(new AgentKey("7:42", "chef"), CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task AFileKeptForOneAgent_IsNotKeptAliveByAnotherAgentsHistoryOfTheSameId()
    {
        _histories.Add(new AgentKey("7:42", "jonas").ToString());

        (await Liveness().IsAliveAsync(new AgentKey("7:42", "chef"), CancellationToken.None)).ShouldBeFalse();
    }

    // Kept before files named their agent: every conversation then was the home's, so the home's
    // agents are the ones to ask.
    [Fact]
    public async Task AFileNamingNoAgent_IsAliveWhileAnyOfTheHomesAgentsHoldsItsConversation()
    {
        _histories.Add(new AgentKey("7:42", "jonas").ToString());

        (await Liveness().IsAliveAsync(new AgentKey("7:42"), CancellationToken.None)).ShouldBeTrue();
        (await Liveness().IsAliveAsync(new AgentKey("8:99"), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task AFileNamingNoAgent_IsAliveWhileNothingHasRegistered()
    {
        (await new AgentConversationLiveness(new MutableAgentCatalog(), _store.Object)
            .IsAliveAsync(new AgentKey("7:42"), CancellationToken.None)).ShouldBeTrue();
    }
}