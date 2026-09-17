using System.Text.Json.Nodes;
using Infrastructure.Clients.HomeAssistant;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// An entity's capabilities — a media player's source_list, a select's options — are persisted in
// the entity registry and served only by the WebSocket command `config/entity_registry/get_entries`,
// so the read runs against the server that speaks the protocol. Only entities with capabilities
// come back; a registry entry without them, or no entry, is absent from the answer.
public class HomeAssistantRegistryClientTests
{
    private static HomeAssistantClient Client(FakeHomeAssistantSocket server) =>
        new(new HttpClient { BaseAddress = new Uri(server.BaseUrl + "/") }, FakeHomeAssistantSocket.ValidToken);

    [Fact]
    public async Task ListCapabilitiesAsync_AsksForTheIds_AndReadsTheCapabilitiesThatExist()
    {
        await using var server = await FakeHomeAssistantSocket.StartAsync();
        server.Capabilities["media_player.receiver"] = new JsonObject { ["source_list"] = new JsonArray("TV", "Bluetooth") };
        server.Capabilities["remote.tv"] = null;

        var capabilities = await Client(server).ListCapabilitiesAsync(["media_player.receiver", "remote.tv", "light.gone"]);

        server.LastCapabilitiesRequest.ShouldBe(["media_player.receiver", "remote.tv", "light.gone"]);
        capabilities.Keys.ShouldBe(["media_player.receiver"]);
        capabilities["media_player.receiver"]["source_list"]!.AsArray().Select(v => v!.GetValue<string>()).ShouldBe(["TV", "Bluetooth"]);
    }

    [Fact]
    public async Task ListCapabilitiesAsync_NoIds_AsksNothing()
    {
        await using var server = await FakeHomeAssistantSocket.StartAsync();

        var capabilities = await Client(server).ListCapabilitiesAsync([]);

        capabilities.ShouldBeEmpty();
        server.AuthCount.ShouldBe(0);
    }
}