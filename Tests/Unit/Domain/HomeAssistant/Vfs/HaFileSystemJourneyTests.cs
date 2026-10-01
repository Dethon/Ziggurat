using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaFileSystemJourneyTests
{
    [Fact]
    public async Task Discover_Inspect_Help_Act()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))) },
            Services = { Service("light", "turn_on", AnyEntityTarget(),
                ("brightness_pct", new HaServiceField { Selector = JsonNode.Parse("""{"number":{"min":1,"max":100}}""") })) },
            AreaTemplateJson = """{"areas":[{"id":"kitchen","name":"Kitchen","entities":["light.kitchen"]}]}"""
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        var globResult = await fs.GlobAsync("entities", "*/", CancellationToken.None);
        globResult.ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries.ShouldContain("entities/light/");

        // 2. inspect state (the exact directory name a listing returns)
        var state = await fs.ReadAsync("entities/light/kitchen_(kitchen)/state.json", null, null, CancellationToken.None);
        state.ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldContain("\"state\": \"off\"");

        var help = await fs.ExecAsync("entities/light/kitchen_(kitchen)", "./turn_on --help", null, CancellationToken.None);
        help.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.Stdout.ShouldContain("--brightness_pct");

        var act = await fs.ExecAsync("entities/light/kitchen_(kitchen)", "./turn_on --brightness_pct 60", null, CancellationToken.None);
        act.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        client.LastCall!.Value.Data!["brightness_pct"]!.GetValue<int>().ShouldBe(60);

        // 4b. a bare id (when a friendly name exists) is rejected with a hint
        var nearMiss = await fs.ExecAsync("entities/light/kitchen", "./turn_on", null, CancellationToken.None);
        var nearMissExec = nearMiss.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        nearMissExec.ExitCode.ShouldBe(127);
        nearMissExec.Stderr.ShouldContain("kitchen_(kitchen)");

        // 5. area view resolves to the same entity via its canonical name
        var areaState = await fs.ReadAsync("areas/kitchen/light.kitchen_(kitchen)/state.json", null, null, CancellationToken.None);
        areaState.ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldContain("\"entity_id\": \"light.kitchen\"");
    }
}