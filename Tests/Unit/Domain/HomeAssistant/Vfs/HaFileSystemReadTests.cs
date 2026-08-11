using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
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
    public async Task ReadAsync_ActionFileForMissingEntity_ReturnsNotFound()
    {
        var fs = Build(out _);
        var result = await fs.ReadAsync("entities/light/ghost/turn_on.sh", null, null, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task GlobAsync_TwoSameClassEntities_AreDistinguishableByName()
    {
        var client = new FakeHaClient
        {
            States =
            {
                Entity("climate.0x01", "cool", ("friendly_name", JsonValue.Create("Aire Acondicionado Salón"))),
                Entity("climate.0x02", "heat", ("friendly_name", JsonValue.Create("Calefacción Salón")))
            },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["climate.0x01","climate.0x02"]}]}"""
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        var result = await fs.GlobAsync("areas/salon", "*/", CancellationToken.None);
        var hits = result.ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries;

        hits.ShouldContain("areas/salon/climate.0x01_(aire-acondicionado-salon)/");
        hits.ShouldContain("areas/salon/climate.0x02_(calefaccion-salon)/");
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
}