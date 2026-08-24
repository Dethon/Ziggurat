using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Exceptions;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaFileSystemReadTests
{
    private static HaFileSystem Build(out FakeHaClient client)
    {
        client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))) },
            Services = { Service("light", "turn_on", AnyEntityTarget()) },
            AreaTemplateJson = """{"areas":[]}"""
        };
        var local = client;
        var provider = new HaCatalogProvider(() => local, new FakeTimeProvider());
        return new HaFileSystem(provider, () => local);
    }

    [Fact]
    public async Task GlobAsync_Directories_ListsEntities()
    {
        var fs = Build(out _);
        var result = await fs.GlobAsync("entities/light", "*/", CancellationToken.None);
        var glob = result.ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        glob.Entries.ShouldContain("entities/light/kitchen_(kitchen)/");
    }

    [Fact]
    public async Task InfoAsync_EntityDir_Exists()
    {
        var fs = Build(out _);
        var result = await fs.InfoAsync("entities/light/kitchen_(kitchen)", CancellationToken.None);
        var info = result.ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        info.Exists.ShouldBeTrue();
        info.IsDirectory.ShouldBe(true);
    }

    [Fact]
    public async Task InfoAsync_MissingEntity_ExistsFalse()
    {
        var fs = Build(out _);
        var result = await fs.InfoAsync("entities/light/ghost", CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value.Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadAsync_StateFile_RendersFreshJson()
    {
        var fs = Build(out _);
        var result = await fs.ReadAsync("entities/light/kitchen_(kitchen)/state.json", null, null, CancellationToken.None);
        var read = result.ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldContain("\"entity_id\": \"light.kitchen\"");
        read.Content.ShouldContain("1: ");
    }

    [Fact]
    public async Task ReadAsync_ActionFile_RendersHelp()
    {
        var fs = Build(out _);
        var result = await fs.ReadAsync("entities/light/kitchen_(kitchen)/turn_on.sh", null, null, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldContain("call light.turn_on on light.kitchen");
    }

    [Fact]
    public async Task InfoAsync_ActionFileForMissingEntity_ExistsFalse()
    {
        var fs = Build(out _);
        var result = await fs.InfoAsync("entities/light/ghost/turn_on.sh", CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value.Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecAsync_ResolvesViaCompositePath()
    {
        var client = new FakeHaClient
        {
            States = { Entity("climate.0x01", "cool", ("friendly_name", JsonValue.Create("Aire Acondicionado Salón"))) },
            Services = { Service("climate", "turn_off", AnyEntityTarget()) },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["climate.0x01"]}]}"""
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        var result = await fs.ExecAsync(
            "areas/salon/climate.0x01_(aire-acondicionado-salon)", "turn_off.sh", null, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        client.LastCall!.Value.EntityId.ShouldBe("climate.0x01");
    }

    [Fact]
    public async Task ReadAsync_BareId_WhenFriendlyNameExists_NotFoundWithHint()
    {
        var fs = Build(out _);
        var result = await fs.ReadAsync("entities/light/kitchen/state.json", null, null, CancellationToken.None);
        var error = result.ShouldBeOfType<FsResult<FsReadResult>.Err>().Error;
        error.ErrorCode.ShouldBe("not_found");
        error.Hint.ShouldNotBeNull().ShouldContain("kitchen_(kitchen)");
    }

    [Fact]
    public async Task ReadAsync_EntityWithoutFriendlyName_ResolvesByBareId()
    {
        var client = new FakeHaClient { States = { Entity("light.porch", "off") } };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);
        var result = await fs.ReadAsync("entities/light/porch/state.json", null, null, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldContain("\"entity_id\": \"light.porch\"");
    }

    [Fact]
    public async Task ReadAsync_SpuriousSuffixOnEntityWithoutFriendlyName_NotFoundWithHint()
    {
        var client = new FakeHaClient { States = { Entity("light.porch", "off") } };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);
        var result = await fs.ReadAsync("entities/light/porch_(garbage)/state.json", null, null, CancellationToken.None);
        var error = result.ShouldBeOfType<FsResult<FsReadResult>.Err>().Error;
        error.ErrorCode.ShouldBe("not_found");
        error.Hint.ShouldNotBeNull().ShouldContain("porch");
    }

    // The position the agent reads drives relative seeks ("rewind three minutes"). HA freezes
    // media_position between state transitions, so a player an hour in still reported where it
    // started and a rewind computed from it clamped to 0. Music Assistant's queue has the real one.
    private static HaFileSystem BuildPlayer(FakeMusicAssistantClient music, out FakeHaClient client, string state = "playing")
    {
        var attrs = JsonNode.Parse($$"""
            {
              "friendly_name": "Office",
              "app_id": "music_assistant",
              "active_queue": "ma_office",
              "media_duration": 5891,
              "media_position": 0,
              "media_position_updated_at": "2026-05-23T08:14:02+00:00"
            }
            """)!.AsObject();

        client = new FakeHaClient
        {
            States =
            {
                new HaEntityState
                {
                    EntityId = "media_player.office",
                    State = state,
                    Attributes = attrs.ToDictionary(a => a.Key, a => a.Value?.DeepClone())
                }
            },
            Services = { Service("media_player", "media_seek", AnyEntityTarget()) },
            AreaTemplateJson = """{"areas":[]}"""
        };
        var local = client;
        return new HaFileSystem(
            new HaCatalogProvider(() => local, new FakeTimeProvider()), () => local, musicClientFactory: () => music);
    }

    private static async Task<string> ReadPlayerAsync(HaFileSystem fs)
    {
        var result = await fs.ReadAsync("entities/media_player/office_(office)/state.json", null, null, CancellationToken.None);
        return result.ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content;
    }

    [Fact]
    public async Task ReadAsync_MusicAssistantPlayer_ReportsTheQueuesLivePosition()
    {
        var music = new FakeMusicAssistantClient
        {
            QueuePositions = { ["ma_office"] = FakeMusicAssistantClient.Position(4243) }
        };

        var content = await ReadPlayerAsync(BuildPlayer(music, out _));

        content.ShouldContain("\"media_position\": 4243");
        content.ShouldContain("\"media_position_source\": \"music_assistant\"");
        music.LastQueueLookup.ShouldBe("ma_office");
    }

    [Fact]
    public async Task ReadAsync_QueueNotPlaying_KeepsHomeAssistantsPosition()
    {
        // A stopped queue's elapsed_time is just what its last transition left behind — the same
        // stale number HA has. Relabelling it as MA-sourced would dress a guess up as live.
        var music = new FakeMusicAssistantClient
        {
            QueuePositions = { ["ma_office"] = FakeMusicAssistantClient.Position(4243, state: "idle") }
        };

        var content = await ReadPlayerAsync(BuildPlayer(music, out _, state: "idle"));

        content.ShouldContain("\"media_position\": 0");
        content.ShouldNotContain("media_position_source");
    }

    [Fact]
    public async Task ReadAsync_MusicAssistantUnreachable_StillReturnsHomeAssistantsState()
    {
        // A state.json that errors is worse than one carrying HA's own value.
        var music = new FakeMusicAssistantClient { Fault = new MusicAssistantException("socket down") };

        var content = await ReadPlayerAsync(BuildPlayer(music, out _));

        content.ShouldContain("\"media_position\": 0");
        content.ShouldNotContain("media_position_source");
    }

    [Fact]
    public async Task ReadAsync_QueueUnknownToMusicAssistant_KeepsHomeAssistantsPosition()
    {
        var content = await ReadPlayerAsync(BuildPlayer(new FakeMusicAssistantClient(), out _));

        content.ShouldContain("\"media_position\": 0");
        content.ShouldNotContain("media_position_source");
    }

    [Fact]
    public async Task ReadAsync_NonMusicAssistantEntity_NeverAsksTheQueue()
    {
        var music = new FakeMusicAssistantClient();
        var fs = Build(out _);

        await fs.ReadAsync("entities/light/kitchen_(kitchen)/state.json", null, null, CancellationToken.None);

        music.LastQueueLookup.ShouldBeNull();
    }
}