using System.Text.Json.Nodes;
using Infrastructure.Clients.HomeAssistant;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// An entity's registry entry — its platform, its config entry, and its capabilities (a media
// player's source_list, a select's options) — is served only by the WebSocket command
// `config/entity_registry/get_entries`, so the read runs against the server that speaks the
// protocol. An entity the registry does not know is absent from the answer; one whose kind persists
// no capabilities is there with none.
public class HomeAssistantRegistryClientTests
{
    private static HomeAssistantClient Client(FakeHomeAssistantSocket server) =>
        new(new HttpClient { BaseAddress = new Uri(server.BaseUrl + "/") }, FakeHomeAssistantSocket.ValidToken);

    [Fact]
    public async Task ListRegistryEntriesAsync_AsksForTheIds_AndReadsTheEntriesThatExist()
    {
        await using var server = await FakeHomeAssistantSocket.StartAsync();
        server.Registry["media_player.receiver"] = new("denon", "entry-denon",
            new JsonObject { ["source_list"] = new JsonArray("TV", "Bluetooth") });
        server.Registry["remote.tv"] = new("androidtv_remote", "entry-tv");

        var entries = await Client(server).ListRegistryEntriesAsync(["media_player.receiver", "remote.tv", "light.gone"]);

        server.LastRegistryRequest.ShouldBe(["media_player.receiver", "remote.tv", "light.gone"]);
        entries.Keys.ShouldBe(["media_player.receiver", "remote.tv"], ignoreOrder: true);
        var receiver = entries["media_player.receiver"];
        receiver.Platform.ShouldBe("denon");
        receiver.ConfigEntryId.ShouldBe("entry-denon");
        receiver.Capabilities!["source_list"]!.AsArray().Select(v => v!.GetValue<string>()).ShouldBe(["TV", "Bluetooth"]);
        var tv = entries["remote.tv"];
        tv.Platform.ShouldBe("androidtv_remote");
        tv.ConfigEntryId.ShouldBe("entry-tv");
        tv.Capabilities.ShouldBeNull();
    }

    [Fact]
    public async Task ListRegistryEntriesAsync_NoIds_AsksNothing()
    {
        await using var server = await FakeHomeAssistantSocket.StartAsync();

        var entries = await Client(server).ListRegistryEntriesAsync([]);

        entries.ShouldBeEmpty();
        server.AuthCount.ShouldBe(0);
    }
}