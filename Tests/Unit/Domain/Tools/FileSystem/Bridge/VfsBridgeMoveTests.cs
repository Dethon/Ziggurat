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
    private readonly VfsBridge _bridge = new(new FakeTimeProvider(DateTimeOffset.UtcNow), new VfsBridgeSettings());

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

    // Every prod mount is the agent's proxy of a remote one, and the bridge takes such a backend as
    // the calling conversation sees it — a view, made on the spot. Two paths on one mount must still
    // be one mount: a view per path made every `mv` inside a mount a copy and a delete, and asked the
    // mount whether a file might leave it on its way to the next directory.
    [Fact]
    public async Task AMoveInsideAMountSeenThroughACallerView_IsStillThatMountsOwnMove()
    {
        var disk = new ViewedDisk("vault", new Dictionary<string, string> { ["a.md"] = "alpha\n" });
        var call = _bridge.Mint(BridgeFixtures.Registry((disk, "/vault", null)), BridgeFixtures.Everything, null, null);

        var answer = await call.RenameAsync("/vault/a.md", "/vault/c.md", overwrite: false, CancellationToken.None);

        answer.ShouldBeOfType<BridgeAnswer<bool>.Ok>();
        disk.Writes.ShouldBe(["move a.md c.md"]);
    }

    [Fact]
    public async Task AReplaceInsideAMountSeenThroughACallerView_IsNotAskedWhetherItMayLeave()
    {
        var disk = new ViewedDisk("media", new Dictionary<string, string>
        {
            ["live/a.txt"] = "new\n",
            ["live/b.txt"] = "old\n"
        })
        {
            RefusesMoveOutOf = "live"
        };
        var call = _bridge.Mint(BridgeFixtures.Registry((disk, "/media", null)), BridgeFixtures.Everything, null, null);

        var answer = await call.RenameAsync("/media/live/a.txt", "/media/live/b.txt", overwrite: true, CancellationToken.None);

        answer.ShouldBeOfType<BridgeAnswer<bool>.Ok>();
        disk.Files["live/b.txt"].ShouldBe("new\n");
    }

    private sealed class ViewedDisk(string name, IDictionary<string, string> files)
        : MemoryDisk(name, files), global::Domain.Contracts.ICallerBoundBackend
    {
        public global::Domain.Contracts.IFileSystemBackend As(global::Domain.DTOs.Channel.ConversationContext? caller) =>
            (global::Domain.Contracts.IFileSystemBackend)MemberwiseClone();
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
        var (_, result) = await RunAsync((call, ct) => call.DeleteAsync("/timers/eggs", ct));

        _timers.Deleted.ShouldBe(["eggs"]);
        Only(result).ShouldBe(("/timers/eggs", VfsChange.Operations.Delete, VfsChange.Statuses.Applied, null));
    }

    [Fact]
    public async Task ADeleteTheRemoveToolWouldAskAbout_IsRefused()
    {
        BridgeAnswer<bool>? answer = null;
        var (_, result) = await RunAsync(
            async (call, ct) => answer = await call.DeleteAsync("/vault/a.md", ct),
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

    // The source goes last, so whether it may go is asked first: a rename onto an existing file
    // whose remove would ask a person is refused with both ends as they were, not after the target
    // was already overwritten.
    [Fact]
    public async Task MvOntoAnExistingFileWhoseSourceTheRemoveToolWouldAskAbout_KeepsBothEnds()
    {
        var (_, result) = await RunAsync(
            (call, ct) => call.RenameAsync("/vault/a.md", "/vault/b.md", overwrite: true, ct),
            BridgeFixtures.Allowing(new[] { VfsMoveTool.Name, VfsFileReadTool.Name, VfsTextCreateTool.Name }
                .Select(FileSystemToolFeature.Callable).ToArray()));

        _vault.Files["b.md"].ShouldBe("beta\n");
        _vault.Files.ShouldContainKey("a.md");
        Only(result).ShouldBe(("/vault/a.md", VfsChange.Operations.Move, VfsChange.Statuses.Refused, "/vault/b.md"));
    }

    // A source its own mount will not let go — a timer's rendered file, which is deleted only with
    // its directory — is found out after the target took its content. The list says both things
    // that happened: the write that landed and the move that did not.
    [Fact]
    public async Task MvOntoAnExistingFileFromASourceItsMountKeeps_ListsTheWriteThatLanded()
    {
        var (_, result) = await RunAsync(
            (call, ct) => call.RenameAsync("/timers/eggs/status.json", "/vault/b.md", overwrite: true, ct));

        _vault.Files["b.md"].ShouldBe("{}");
        var changes = result["vfsChanges"]!.AsArray()
            .Select(c => (c!["path"]!.GetValue<string>(), c["operation"]!.GetValue<string>(), c["status"]!.GetValue<string>()))
            .ToList();
        changes.ShouldBe(
        [
            ("/vault/b.md", VfsChange.Operations.Write, VfsChange.Statuses.Applied),
            ("/timers/eggs/status.json", VfsChange.Operations.Move, VfsChange.Statuses.Refused)
        ]);
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