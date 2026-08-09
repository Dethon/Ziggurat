using Domain.DTOs.WebChat;
using Shouldly;

namespace Tests.Unit.WebChat.Client.Fixtures;

// Proof that the fixture is wired. Everything else about this feature is asserted on the state a
// user would see; this one is about the composition itself.
public sealed class ScriptedChatClientTests
{
    [Fact]
    public async Task AHubCall_ThroughATransportThatIsNotLive_NeverReachesIt()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        client.GoNotLive();

        var result = await client.LiveConnection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
            "GetAllTopics", "agent-1", "hearth");

        result.IsLive.ShouldBeFalse();
        transport.Calls.ShouldBeEmpty();
    }
}