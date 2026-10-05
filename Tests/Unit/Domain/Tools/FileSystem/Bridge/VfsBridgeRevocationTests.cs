using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem.Bridge;

// A command killed by its timeout commits nothing: the launcher revokes the call's token before it
// kills the tree, so the writes the kill itself flushes — the kernel releases a dead process's open
// files — arrive revoked and are dropped, and the exec result says so.
public class VfsBridgeRevocationTests
{
    private readonly VfsBridge _bridge = new(new FakeTimeProvider(DateTimeOffset.UtcNow));

    private readonly MemoryDisk _vault = new("vault", new Dictionary<string, string> { ["a.md"] = "old\n" });

    private VfsExecTool Tool(Func<VfsCall, CancellationToken, Task<FsExecResult>> script) =>
        new(BridgeFixtures.Registry(
            (new ScriptedSandbox(_bridge, script), ScriptedSandbox.Mount, ShellReach.Contained),
            (_vault, "/vault", null)), _bridge);

    [Fact]
    public async Task CommitsAfterTheRevocation_AreDroppedAndListedAsDropped()
    {
        var result = await Tool(async (call, ct) =>
        {
            await call.WriteAsync("/vault/before.md", "kept\n"u8.ToArray(), isNew: true, ct);
            _bridge.Revoke(call.Token);
            await call.WriteAsync("/vault/a.md", "half\n"u8.ToArray(), isNew: false, ct);
            await call.DeleteAsync("/vault/before.md", ct);
            await call.RenameAsync("/vault/a.md", "/vault/b.md", overwrite: false, ct);
            return BridgeFixtures.Ran("") with { TimedOut = true, ExitCode = -1 };
        }).RunAsync("/sandbox", "slow", arguments: BridgeFixtures.Whitelisted);

        _vault.Files["a.md"].ShouldBe("old\n");
        _vault.Files["before.md"].ShouldBe("kept\n");
        result["vfsChanges"]!.AsArray().Select(c => (c!["path"]!.GetValue<string>(), c["status"]!.GetValue<string>()))
            .ShouldBe([
                ("/vault/before.md", VfsChange.Statuses.Applied),
                ("/vault/a.md", VfsChange.Statuses.Dropped),
                ("/vault/before.md", VfsChange.Statuses.Dropped),
                ("/vault/a.md", VfsChange.Statuses.Dropped)
            ]);
    }

    // A revoked token still answers, so what arrives after the kill is recorded rather than lost;
    // only the exec returning ends it.
    [Fact]
    public void ARevokedToken_StillAnswersUntilTheExecCompletesIt()
    {
        var call = _bridge.Mint(BridgeFixtures.Registry((_vault, "/vault", null)), _ => true, null);

        _bridge.Revoke(call.Token);

        _bridge.Find(call.Token).ShouldNotBeNull().Revoked.ShouldBeTrue();
        _bridge.Complete(call.Token);
        _bridge.Find(call.Token).ShouldBeNull();
    }

    // A whitelist covering the equivalent tool — or an approval the person chose to remember — lets
    // the shell write through, unasked.
    [Fact]
    public async Task AToolThatRunsUnasked_LetsTheShellWriteThrough()
    {
        var result = await Tool(async (call, ct) =>
        {
            await call.WriteAsync("/vault/a.md", "new\n"u8.ToArray(), isNew: false, ct);
            return BridgeFixtures.Ran("");
        }).RunAsync("/sandbox", "echo new > /vault/a.md",
            arguments: BridgeFixtures.Allowing(FileSystemToolFeature.Callable(VfsTextCreateTool.Name)));

        _vault.Files["a.md"].ShouldBe("new\n");
        result["vfsChanges"]![0]!["status"]!.GetValue<string>().ShouldBe(VfsChange.Statuses.Applied);
    }
}