using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem.Bridge;

// A command's deletes and moves through the bridge: each is the call the remove or move tool would
// make, and a move between two mounts is the move tool's transfer, so the source mount is asked
// whether the path may leave before anything is copied.
public class VfsBridgeMoveTests
{
    private readonly VfsBridge _bridge = new(new FakeTimeProvider(DateTimeOffset.UtcNow));

    private readonly MemoryDisk _vault = new("vault", new Dictionary<string, string>
    {
        ["a.md"] = "alpha\n",
        ["b.md"] = "beta\n"
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
        ["eggs/status.json"] = "{}",
        ["eggs/timer.json"] = "{}"
    });

    private async Task<(VfsCall Call, System.Text.Json.Nodes.JsonNode Result)> RunAsync(
        Func<VfsCall, CancellationToken, Task> script, AIFunctionArgumentsWithPermission? arguments = null)
    {
        VfsCall? seen = null;
        var tool = new VfsExecTool(BridgeFixtures.Registry(
            (new ScriptedSandbox(_bridge, async (call, ct) =>
            {
                seen = call;
                await script(call, ct);
                return BridgeFixtures.Ran("");
            }), ScriptedSandbox.Mount, ShellReach.Contained),
            (_vault, "/vault", null),
            (_media, "/media", null),
            (_timers, "/timers", null)), _bridge);
        var result = await tool.RunAsync("/sandbox", "script", arguments: arguments ?? BridgeFixtures.Whitelisted);
        return (seen!, result);
    }

    private static (string, string, string, string?) Only(System.Text.Json.Nodes.JsonNode result)
    {
        var change = result["vfsChanges"]!.AsArray().ShouldHaveSingleItem()!;
        return (change["path"]!.GetValue<string>(), change["operation"]!.GetValue<string>(),
            change["status"]!.GetValue<string>(), change["destination"]?.GetValue<string>());
    }

    // `rm -r /timers/eggs` reaches the bridge as the one delete of the directory: the timer's
    // cancel, exactly as the remove tool makes it.
    [Fact]
    public async Task DeletingATimersDirectory_CancelsIt()
    {
        var (_, result) = await RunAsync((call, ct) => call.DeleteAsync("/timers/eggs", directory: true, ct));

        _timers.Deleted.ShouldBe(["eggs"]);
        Only(result).ShouldBe(("/timers/eggs", VfsChange.Operations.Delete, VfsChange.Statuses.Applied, null));
    }

    [Fact]
    public async Task ADeleteTheRemoveToolWouldAskAbout_IsRefused()
    {
        BridgeAnswer<bool>? answer = null;
        var (_, result) = await RunAsync(
            async (call, ct) => answer = await call.DeleteAsync("/vault/a.md", directory: false, ct),
            BridgeFixtures.Allowing(FileSystemToolFeature.Callable(VfsFileReadTool.Name)));

        answer.ShouldBeOfType<BridgeAnswer<bool>.Refused>().Errno.ShouldBe(Errnos.Denied);
        _vault.Files.ShouldContainKey("a.md");
        Only(result).ShouldBe(("/vault/a.md", VfsChange.Operations.Delete, VfsChange.Statuses.Refused, null));
    }

    // Within one mount, a rename is that mount's move — its move rules apply — never a delete and
    // a create.
    [Fact]
    public async Task MvWithinAMount_IsItsMove()
    {
        var (_, result) = await RunAsync((call, ct) => call.RenameAsync("/vault/a.md", "/vault/c.md", overwrite: false, ct));

        _vault.Writes.ShouldBe(["move a.md c.md"]);
        Only(result).ShouldBe(("/vault/a.md", VfsChange.Operations.Move, VfsChange.Statuses.Applied, "/vault/c.md"));
    }

    [Fact]
    public async Task MvBetweenMounts_IsATransfer()
    {
        var (_, result) = await RunAsync((call, ct) => call.RenameAsync("/media/done.txt", "/vault/done.txt", overwrite: false, ct));

        _vault.Files["done.txt"].ShouldBe("whole\n");
        _media.Files.ShouldNotContainKey("done.txt");
        Only(result).ShouldBe(("/media/done.txt", VfsChange.Operations.Move, VfsChange.Statuses.Applied, "/vault/done.txt"));
    }

    // The move-out check is asked before anything is copied, and its refusal keeps the source.
    [Fact]
    public async Task MvBetweenMountsTheSourceRefusesToLose_KeepsTheSource()
    {
        BridgeAnswer<bool>? answer = null;
        var (_, result) = await RunAsync(async (call, ct) =>
            answer = await call.RenameAsync("/media/downloads/live/film.txt", "/vault/film.txt", overwrite: false, ct));

        answer.ShouldBeOfType<BridgeAnswer<bool>.Refused>().Errno.ShouldBe(Errnos.Denied);
        _media.Files.ShouldContainKey("downloads/live/film.txt");
        _vault.Files.ShouldNotContainKey("film.txt");
        var change = result["vfsChanges"]!.AsArray().ShouldHaveSingleItem()!;
        change["status"]!.GetValue<string>().ShouldBe(VfsChange.Statuses.Refused);
        change["error"]!["message"]!.GetValue<string>().ShouldContain("cannot leave");
    }

    // Onto an existing file a rename is judged as a write there: the target takes the source's
    // content under the write's rules, and the source goes.
    [Fact]
    public async Task MvOntoAnExistingFile_IsAWriteToIt()
    {
        var (_, result) = await RunAsync((call, ct) => call.RenameAsync("/vault/a.md", "/vault/b.md", overwrite: true, ct));

        _vault.Files["b.md"].ShouldBe("alpha\n");
        _vault.Files.ShouldNotContainKey("a.md");
        Only(result).ShouldBe(("/vault/a.md", VfsChange.Operations.Move, VfsChange.Statuses.Applied, "/vault/b.md"));
    }

    // Onto something already there on another mount, the source is still asked whether the path
    // may leave before anything is written: a refusal keeps both ends as they were.
    [Fact]
    public async Task MvOntoAnExistingFileOnAnotherMount_AsksTheMoveOutCheckFirst()
    {
        _vault.Files["film.txt"] = "old copy\n";

        var (_, result) = await RunAsync((call, ct) =>
            call.RenameAsync("/media/downloads/live/film.txt", "/vault/film.txt", overwrite: true, ct));

        _vault.Files["film.txt"].ShouldBe("old copy\n");
        _media.Files.ShouldContainKey("downloads/live/film.txt");
        Only(result).ShouldBe(("/media/downloads/live/film.txt", VfsChange.Operations.Move, VfsChange.Statuses.Refused, "/vault/film.txt"));
    }

    // A move the move tool would ask about is refused before either mount is touched.
    [Fact]
    public async Task AMoveTheMoveToolWouldAskAbout_IsRefused()
    {
        var (_, result) = await RunAsync(
            (call, ct) => call.RenameAsync("/vault/a.md", "/vault/c.md", overwrite: false, ct),
            BridgeFixtures.Allowing(FileSystemToolFeature.Callable(VfsFileReadTool.Name)));

        _vault.Files.ShouldContainKey("a.md");
        Only(result).ShouldBe(("/vault/a.md", VfsChange.Operations.Move, VfsChange.Statuses.Refused, "/vault/c.md"));
    }
}