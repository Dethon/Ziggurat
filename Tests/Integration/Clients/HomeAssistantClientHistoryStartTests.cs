using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// What the first element of a history is, pinned against the real recorder: the state the entity
// had when the window opened, stamped at the window's start — not at the instant it last changed,
// which may be days earlier. The action's help says so, and this is the evidence.
public class HomeAssistantClientHistoryStartTests(HomeAssistantFixture fixture) : IClassFixture<HomeAssistantFixture>
{
    [Fact]
    public async Task ListHistoryAsync_FirstElement_IsTheStateAtTheWindowsStart_StampedThere()
    {
        var client = fixture.CreateClient();
        await client.CallServiceAsync("input_boolean", "toggle", HomeAssistantFixture.TestEntityId, null);
        await Task.Delay(3000);
        var start = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        await Task.Delay(1500);
        var end = DateTimeOffset.UtcNow.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        var changes = await client.ListHistoryAsync(HomeAssistantFixture.TestEntityId, start, end);

        changes.ShouldHaveSingleItem();
        changes[0].At.ShouldBe(DateTimeOffset.Parse(start));
    }
}