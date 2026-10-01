using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.Unit.Domain.Tools.FileSystem.Bridge;

namespace Tests.E2E.Sandbox;

// rm and mv through the shell, in the real image: one meaning per action on every route.
[Trait("Category", "E2E")]
[Collection(SandboxE2ECollection.Name)]
public class SandboxDeletesAndMovesE2ETests(SandboxE2EFixture fixture)
{
    private readonly MemoryDisk _vault = new("vault", new Dictionary<string, string>
    {
        ["a.md"] = "alpha\n",
        ["keep/b.md"] = "beta\n"
    });

    private readonly MemoryDisk _media = new("media", new Dictionary<string, string>
    {
        ["downloads/live/film.txt"] = "half\n",
        ["done.txt"] = "whole\n"
    })
    {
        RefusesMoveOutOf = "downloads/live"
    };

    private readonly RenderedMount _timers = new("timers", new Dictionary<string, string>
    {
        ["eggs/status.json"] = """{"remainingSeconds":120}""",
        ["eggs/timer.json"] = """{"seconds":300}"""
    });

    private VfsCall Mint() => fixture.Bridge.Mint(
        BridgeFixtures.Registry((_vault, "/vault", null), (_media, "/media", null), (_timers, "/timers", null)), _ => true);

    private static IReadOnlyList<(string, string, string, string?)> Changes(VfsCall call) =>
        [.. call.Changes.Select(c => (c.Path, c.Operation, c.Status, c.Destination))];

    // rm -r unlinks the timer's files before it removes the directory; held, those reach the timer
    // mount as the one delete of the directory, which cancels it as the remove tool does.
    [SkippableFact]
    public async Task RmROfATimer_CancelsIt()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        var result = await ExecAsync(client, "rm -r /timers/eggs && ls -A /timers | wc -l", call, cts.Token);

        Stdout(result).ShouldBe("0\n", result.ToString());
        _timers.Deleted.ShouldBe(["eggs"]);
        Changes(call).ShouldBe([("/timers/eggs", "delete", "applied", null)]);
    }

    [SkippableFact]
    public async Task MvWithinTheVault_IsItsMove()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        await ExecAsync(client, "mv /vault/a.md /vault/keep/a.md", call, cts.Token);

        _vault.Writes.ShouldBe(["move a.md keep/a.md"]);
        Changes(call).ShouldBe([("/vault/a.md", "move", "applied", "/vault/keep/a.md")]);
    }

    [SkippableFact]
    public async Task MvBetweenTwoMounts_IsATransfer()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        await ExecAsync(client, "mv /media/done.txt /vault/done.txt", call, cts.Token);

        _vault.Files["done.txt"].ShouldBe("whole\n");
        _media.Files.ShouldNotContainKey("done.txt");
        Changes(call).ShouldBe([("/media/done.txt", "move", "applied", "/vault/done.txt")]);
    }

    // The move-out check runs before anything is copied; mv reports the failure and the source
    // stays where it was.
    [SkippableFact]
    public async Task MvOfAPathTheSourceRefusesToLose_KeepsIt()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();

        var result = await ExecAsync(client, "mv /media/downloads/live/film.txt /vault/film.txt; echo exit=$?", call, cts.Token);

        Stdout(result).ShouldContain("exit=1");
        _media.Files.ShouldContainKey("downloads/live/film.txt");
        _vault.Files.ShouldNotContainKey("film.txt");
        Changes(call).ShouldBe([("/media/downloads/live/film.txt", "move", "refused", "/vault/film.txt")]);
    }

    // The home and /vfs are different filesystems, so the kernel makes mv a copy and an unlink:
    // the mount sees a create one way and a delete the other.
    [SkippableFact]
    public async Task MvBetweenTheHomeAndAMount_IsACreateOrADelete()
    {
        Skip.IfNot(fixture.Available, "Docker is not available");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await fixture.ConnectAsync(cts.Token);
        var call = Mint();
        var name = $"mv-{Guid.NewGuid():N}";

        await ExecAsync(client,
            $"echo in > ~/{name}.md && mv ~/{name}.md /vault/{name}.md && mv /vault/a.md ~/{name}-a.md && cat ~/{name}-a.md",
            call, cts.Token);

        _vault.Files[$"{name}.md"].ShouldBe("in\n");
        _vault.Files.ShouldNotContainKey("a.md");
        Changes(call).ShouldBe([
            ("/vault/a.md", "delete", "applied", null),
            ($"/vault/{name}.md", "create", "applied", null)
        ], ignoreOrder: true);
    }

    private static string Stdout(JsonElement result) =>
        result.TryGetProperty("stdout", out var stdout) ? stdout.GetString()! : result.ToString();

    private static async Task<JsonElement> ExecAsync(McpClient client, string command, VfsCall call, CancellationToken ct)
    {
        var result = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = "fs_exec",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(""),
                ["command"] = JsonSerializer.SerializeToElement(command)
            },
            Meta = new JsonObject { [VfsBridgeGrant.MetaKey] = new VfsBridgeGrant(call.Token).ToMeta() }
        }, cancellationToken: ct);

        return JsonDocument.Parse(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)))
            .RootElement.Clone();
    }
}