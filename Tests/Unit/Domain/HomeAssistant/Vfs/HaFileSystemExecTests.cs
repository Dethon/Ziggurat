using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Exceptions;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaFileSystemExecTests
{
    private static HaFileSystem Build(out FakeHaClient client)
    {
        client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off") },
            Services = { Service("light", "turn_on", AnyEntityTarget(),
                ("brightness_pct", new HaServiceField { Selector = JsonNode.Parse("""{"number":{"min":1,"max":100}}""") })) }
        };
        var local = client;
        var provider = new HaCatalogProvider(() => local, new FakeTimeProvider());
        return new HaFileSystem(provider, () => local);
    }

    [Fact]
    public async Task Exec_CallsService_WithParsedData()
    {
        var fs = Build(out var client);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --brightness_pct 60", null, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        client.LastCall!.Value.Domain.ShouldBe("light");
        client.LastCall.Value.Service.ShouldBe("turn_on");
        client.LastCall.Value.EntityId.ShouldBe("light.kitchen");
        client.LastCall.Value.Data!["brightness_pct"]!.GetValue<int>().ShouldBe(60);
    }

    [Fact]
    public async Task Exec_Help_ReturnsUsage_ExitZero_NoCall()
    {
        var fs = Build(out var client);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --help", null, CancellationToken.None);

        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(0);
        exec.Stdout.ShouldContain("--brightness_pct");
        client.LastCall.ShouldBeNull();
    }

    [Fact]
    public async Task Exec_DotSlashPrefixAccepted()
    {
        var fs = Build(out var client);
        var result = await fs.ExecAsync("entities/light/kitchen", "./turn_on.sh", null, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        client.LastCall.ShouldNotBeNull();
    }

    [Fact]
    public async Task Exec_UnknownCommand_Returns127_WithAvailableActions()
    {
        var fs = Build(out _);
        var result = await fs.ExecAsync("entities/light/kitchen", "cat state.json", null, CancellationToken.None);

        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(127);
        exec.Stderr.ShouldContain("turn_on.sh");
    }

    [Fact]
    public async Task Exec_BadArg_Returns2()
    {
        var fs = Build(out _);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --nope 1", null, CancellationToken.None);
        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(2);
        exec.Stderr.ShouldContain("nope");
    }

    [Fact]
    public async Task Exec_NotAnEntityDir_Returns127()
    {
        var fs = Build(out _);
        var result = await fs.ExecAsync("entities/light", "turn_on.sh", null, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(127);
    }

    [Fact]
    public async Task Exec_HaFailure_Returns1_WithHint()
    {
        var fs = Build(out var client);
        client.CallHandler = (_, _, _, _) => throw new HomeAssistantException("400 bad field", 400);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --brightness_pct 60", null, CancellationToken.None);

        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(1);
        exec.Stderr.ShouldContain("400 bad field");
        exec.Stderr.ShouldContain("--help");
    }

    [Fact]
    public async Task Exec_HaServerSideFailure_Returns1_WithResolveHint()
    {
        var fs = Build(out var client);
        client.CallHandler = (_, _, _, _) =>
            throw new HomeAssistantException("Home Assistant returned 500: Server got itself in trouble", 500);
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --brightness_pct 60", null, CancellationToken.None);

        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(1);
        exec.Stderr.ShouldContain("500");
        exec.Stderr.ShouldContain("browse_media.sh");
        exec.Stderr.ShouldNotContain("field types");
    }

    [Fact]
    public async Task Exec_HaFailure_NoStatusCode_Returns1_MessageOnly()
    {
        var fs = Build(out var client);
        client.CallHandler = (_, _, _, _) => throw new HomeAssistantException("boom");
        var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh", null, CancellationToken.None);

        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(1);
        exec.Stderr.ShouldBe("boom");
    }

    [Fact]
    public async Task ExecAsync_HonorsTimeout_ReturnsTimedOut()
    {
        var client = new BlockingHaClient
        {
            States = { Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))) },
            Services = { Service("light", "turn_on", AnyEntityTarget()) }
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        var result = await fs.ExecAsync("entities/light/kitchen_(kitchen)", "turn_on.sh", 1, CancellationToken.None);

        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.TimedOut.ShouldBeTrue();
        exec.ExitCode.ShouldBe(124); // GNU `timeout` convention
    }

    [Fact]
    public async Task ExecAsync_UnknownEntity_127_NoHint()
    {
        var fs = Build(out _);
        var result = await fs.ExecAsync("entities/light/ghost", "turn_on.sh", null, CancellationToken.None);
        var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        exec.ExitCode.ShouldBe(127);
        exec.Stderr.ShouldNotContain("Did you mean");
    }

    [Fact]
    public async Task Exec_RoutesCrossDomainMusicAssistantService_ByQualifiedName()
    {
        // The same-domain `media_player.play_media` (-> play_media.sh) needs a concrete
        // media_content_id; Music Assistant's `music_assistant.play_media` (-> the domain-qualified
        // music_assistant.play_media.sh) resolves a free-text name. Both must coexist without the
        // qualified name colliding with the bare one, and exec must route to the right domain/service.
        var client = new FakeHaClient
        {
            States = { Entity("media_player.office", "idle") },
            Services =
            {
                Service("media_player", "play_media", DomainTarget("media_player"),
                    ("media_content_id", new HaServiceField { Required = true }),
                    ("media_content_type", new HaServiceField { Required = true })),
                Service("music_assistant", "play_media", DomainTarget("media_player"),
                    ("media_id", new HaServiceField { Required = true, Selector = JsonNode.Parse("""{"object":{"multiple":false}}""") }))
            }
        };
        var fs = new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);

        var result = await fs.ExecAsync("entities/media_player/office",
            "music_assistant.play_media.sh --media_id \"miles davis\"", null, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        client.LastCall!.Value.Domain.ShouldBe("music_assistant");
        client.LastCall.Value.Service.ShouldBe("play_media");
        client.LastCall.Value.EntityId.ShouldBe("media_player.office");
        client.LastCall.Value.Data!["media_id"]!.GetValue<string>().ShouldBe("miles davis");
    }

    [Fact]
    public async Task Exec_MediaSeekToZero_SendsOneSecond()
    {
        // Music Assistant's play_index treats `seek_position` 0 as "no seek requested" and silently
        // substitutes the item's stored resume point, so seeking a podcast episode to 0 lands where
        // the listener already was. 1 second is truthy there and inaudible here.
        var fs = BuildSeekable(out var client);

        var result = await fs.ExecAsync("entities/media_player/office",
            "media_seek.sh --seek_position 0", null, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        client.LastCall!.Value.Data!["seek_position"]!.GetValue<double>().ShouldBe(1);
    }

    [Fact]
    public async Task Exec_MediaSeekToNonZero_SendsPositionUnchanged()
    {
        var fs = BuildSeekable(out var client);

        var result = await fs.ExecAsync("entities/media_player/office",
            "media_seek.sh --seek_position 42.5", null, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        client.LastCall!.Value.Data!["seek_position"]!.GetValue<double>().ShouldBe(42.5);
    }

    [Fact]
    public async Task Exec_ZeroOnAnotherService_SendsZeroUnchanged()
    {
        var fs = Build(out var client);

        var result = await fs.ExecAsync("entities/light/kitchen",
            "turn_on.sh --brightness_pct 0", null, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value.ExitCode.ShouldBe(0);
        client.LastCall!.Value.Data!["brightness_pct"]!.GetValue<double>().ShouldBe(0);
    }

    private static HaFileSystem BuildSeekable(out FakeHaClient client)
    {
        client = new FakeHaClient
        {
            States = { Entity("media_player.office", "playing") },
            Services =
            {
                Service("media_player", "media_seek", DomainTarget("media_player"),
                    ("seek_position", new HaServiceField
                    {
                        Required = true,
                        Selector = JsonNode.Parse("""{"number":{"min":0,"max":9223372036854775807}}""")
                    }))
            }
        };
        var local = client;
        return new HaFileSystem(new HaCatalogProvider(() => local, new FakeTimeProvider()), () => local);
    }

    private sealed class BlockingHaClient : FakeHaClient
    {
        public override async Task<HaServiceCallResult> CallServiceAsync(
            string domain, string service, string? entityId,
            IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HaServiceCallResult { ChangedEntities = [] };
        }
    }
}