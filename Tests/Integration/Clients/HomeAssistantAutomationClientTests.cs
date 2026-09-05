using System.Text.Json.Nodes;
using Domain.Exceptions;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// The automation config API against a real Home Assistant: a watch is a real automation, so what
// matters is that the home takes the config, lists the entity it creates, and refuses a bad one
// with words the agent can act on.
public class HomeAssistantAutomationClientTests(HomeAssistantFixture fixture) : IClassFixture<HomeAssistantFixture>
{
    [Fact]
    public async Task WriteReadListDelete_RoundTripsThroughTheRealHome()
    {
        var client = fixture.CreateClient();
        var id = $"assistant_watch_itest_{Guid.NewGuid():N}";

        await client.UpsertAutomationConfigAsync(id, new JsonObject
        {
            ["alias"] = "Integration test watch",
            ["description"] = """{"watch":true}""",
            ["mode"] = "single",
            ["triggers"] = new JsonArray(new JsonObject
            {
                ["trigger"] = "state",
                ["entity_id"] = HomeAssistantFixture.TestEntityId,
                ["to"] = "on"
            }),
            ["actions"] = new JsonArray(new JsonObject
            {
                ["action"] = "input_boolean.turn_off",
                ["target"] = new JsonObject { ["entity_id"] = HomeAssistantFixture.TestEntityId }
            })
        });

        var config = await client.GetAutomationConfigAsync(id);
        config.ShouldNotBeNull();
        config["alias"]!.GetValue<string>().ShouldBe("Integration test watch");
        config["description"]!.GetValue<string>().ShouldBe("""{"watch":true}""");

        // The config write reloads the automation, so its entity is listed straight afterwards.
        await Eventually.Until(
            async () => (await client.ListAutomationsAsync()).Any(a => a.ConfigId == id),
            "the written automation to be listed as an entity");
        var listed = (await client.ListAutomationsAsync()).Single(a => a.ConfigId == id);
        listed.IsOn.ShouldBeTrue();
        listed.FriendlyName.ShouldBe("Integration test watch");
        listed.LastTriggered.ShouldBeNull();

        await client.DeleteAutomationConfigAsync(id);

        (await client.GetAutomationConfigAsync(id)).ShouldBeNull();
        await Eventually.Until(
            async () => (await client.ListAutomationsAsync()).All(a => a.ConfigId != id),
            "the deleted automation's entity to be gone");
    }

    [Fact]
    public async Task WritingAnInvalidTrigger_FailsWithHomeAssistantsValidationMessage()
    {
        var client = fixture.CreateClient();
        var id = $"assistant_watch_itest_{Guid.NewGuid():N}";

        var ex = await Should.ThrowAsync<HomeAssistantConfigRejectedException>(() =>
            client.UpsertAutomationConfigAsync(id, new JsonObject
            {
                ["alias"] = "Broken",
                ["mode"] = "single",
                ["triggers"] = new JsonArray(new JsonObject { ["trigger"] = "numeric_state" }),
                ["actions"] = new JsonArray()
            }));

        ex.Message.ShouldContain("entity_id");
        (await client.GetAutomationConfigAsync(id)).ShouldBeNull();
    }
}