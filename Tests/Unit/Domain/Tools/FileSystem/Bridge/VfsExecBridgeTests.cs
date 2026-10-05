using System.Text;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem.Bridge;

// The exec tool and the bridge together, with the sandbox replaced by a script that replays the
// kernel's operations against the bridge using the token the tool minted. Asserted at the seams a
// caller observes: the bridge's answers and the exec result.
public class VfsExecBridgeTests
{
    private readonly VfsBridge _bridge = new(new FakeTimeProvider(DateTimeOffset.UtcNow), new VfsBridgeSettings());

    private readonly MemoryDisk _vault = new("vault", new Dictionary<string, string>
    {
        ["notes/todo.md"] = "- [ ] TODO buy milk\n",
        ["notes/done.md"] = "nothing\n",
        ["inbox.md"] = "hello\n"
    });

    private readonly RenderedMount _timers = new("timers", new Dictionary<string, string>
    {
        ["eggs/status.json"] = """{"label":"eggs","remainingSeconds":120}"""
    });

    private readonly MemoryDisk _laptop = new("laptop", new Dictionary<string, string> { ["secret.txt"] = "mine" });

    private (VfsExecTool Tool, ScriptedSandbox Sandbox) Build(Func<VfsCall, CancellationToken, Task<FsExecResult>> script)
    {
        var sandbox = new ScriptedSandbox(_bridge, script);
        var registry = BridgeFixtures.Registry(
            (sandbox, ScriptedSandbox.Mount, ShellReach.Contained),
            (_vault, "/vault", null),
            (_timers, "/timers", null),
            (_laptop, "outpost:laptop", null));
        return (new VfsExecTool(registry, _bridge), sandbox);
    }

    [Fact]
    public async Task ACommandOnTheSandbox_ReadsTheVaultThroughTheTokenTheToolMinted()
    {
        byte[]? read = null;
        var (tool, _) = Build(async (call, ct) =>
        {
            read = (await call.ReadAsync("/vault/notes/todo.md", ct)).ShouldBeOfType<BridgeAnswer<byte[]>.Ok>().Value;
            return BridgeFixtures.Ran("");
        });

        await tool.RunAsync("/sandbox", "cat /vault/notes/todo.md", arguments: BridgeFixtures.Whitelisted);

        Encoding.UTF8.GetString(read!).ShouldBe("- [ ] TODO buy milk\n");
    }

    [Fact]
    public async Task ARenderedFile_IsReadWhole_AndItsSizeIsLeftUnknown()
    {
        BridgeAttr? attr = null;
        byte[]? read = null;
        var (tool, _) = Build(async (call, ct) =>
        {
            attr = (await call.AttrAsync("/timers/eggs/status.json", ct)).ShouldBeOfType<BridgeAnswer<BridgeAttr>.Ok>().Value;
            read = (await call.ReadAsync("/timers/eggs/status.json", ct)).ShouldBeOfType<BridgeAnswer<byte[]>.Ok>().Value;
            return BridgeFixtures.Ran("");
        });

        await tool.RunAsync("/sandbox", "jq . /timers/eggs/status.json", arguments: BridgeFixtures.Whitelisted);

        attr.ShouldBe(new BridgeAttr(BridgeKinds.File, null));
        Encoding.UTF8.GetString(read!).ShouldBe("""{"label":"eggs","remainingSeconds":120}""");
    }

    [Fact]
    public async Task ADiskFile_ReportsTheSizeTheMountGives()
    {
        BridgeAttr? attr = null;
        var (tool, _) = Build(async (call, ct) =>
        {
            attr = (await call.AttrAsync("/vault/inbox.md", ct)).ShouldBeOfType<BridgeAnswer<BridgeAttr>.Ok>().Value;
            return BridgeFixtures.Ran("");
        });

        await tool.RunAsync("/sandbox", "stat /vault/inbox.md", arguments: BridgeFixtures.Whitelisted);

        attr.ShouldBe(new BridgeAttr(BridgeKinds.File, 6));
    }

    [Fact]
    public async Task ListingADirectory_IsWhatGlobLists()
    {
        BridgeListing? listing = null;
        var (tool, _) = Build(async (call, ct) =>
        {
            listing = (await call.ListAsync("/vault", ct)).ShouldBeOfType<BridgeAnswer<BridgeListing>.Ok>().Value;
            return BridgeFixtures.Ran("");
        });

        await tool.RunAsync("/sandbox", "ls /vault", arguments: BridgeFixtures.Whitelisted);

        listing!.Entries.ShouldBe([
            new BridgeEntry("inbox.md", BridgeKinds.File),
            new BridgeEntry("notes", BridgeKinds.Directory)
        ], ignoreOrder: true);
        listing.Truncated.ShouldBeFalse();
    }

    // /vfs holds every mount the session has but two: an outpost is a separate machine, and the
    // sandbox is the shell's own disk.
    [Fact]
    public async Task TheRoot_ListsEveryServedMount_AndNeitherAnOutpostNorTheSandbox()
    {
        BridgeListing? listing = null;
        BridgeAnswer<byte[]>? outpost = null;
        var (tool, _) = Build(async (call, ct) =>
        {
            listing = (await call.ListAsync("/", ct)).ShouldBeOfType<BridgeAnswer<BridgeListing>.Ok>().Value;
            outpost = await call.ReadAsync("/laptop/secret.txt", ct);
            return BridgeFixtures.Ran("");
        });

        await tool.RunAsync("/sandbox", "ls /vfs", arguments: BridgeFixtures.Whitelisted);

        listing!.Entries.Select(e => e.Name).ShouldBe(["timers", "vault"]);
        outpost.ShouldBeOfType<BridgeAnswer<byte[]>.Refused>().Errno.ShouldBe(Errnos.NotFound);
    }

    // A read is a file_read: where the tool would put the question to a person, the command is
    // refused rather than left waiting on one.
    [Fact]
    public async Task AReadTheFileToolWouldAskAbout_IsRefused()
    {
        BridgeAnswer<byte[]>? read = null;
        BridgeAnswer<BridgeListing>? listed = null;
        var (tool, _) = Build(async (call, ct) =>
        {
            read = await call.ReadAsync("/vault/inbox.md", ct);
            listed = await call.ListAsync("/vault", ct);
            return BridgeFixtures.Ran("");
        });

        await tool.RunAsync("/sandbox", "cat /vault/inbox.md",
            arguments: BridgeFixtures.Allowing(FileSystemToolFeature.Callable(VfsGlobFilesTool.Name)));

        read.ShouldBeOfType<BridgeAnswer<byte[]>.Refused>().Errno.ShouldBe(Errnos.Denied);
        listed.ShouldBeOfType<BridgeAnswer<BridgeListing>.Ok>();
    }

    // A call with no approval view behind it cannot know what would run unasked, so nothing does.
    [Fact]
    public async Task ACallWithNoApprovalView_IsRefusedEverything()
    {
        BridgeAnswer<byte[]>? read = null;
        var (tool, _) = Build(async (call, ct) =>
        {
            read = await call.ReadAsync("/vault/inbox.md", ct);
            return BridgeFixtures.Ran("");
        });

        await tool.RunAsync("/sandbox", "cat /vault/inbox.md");

        read.ShouldBeOfType<BridgeAnswer<byte[]>.Refused>().Errno.ShouldBe(Errnos.Denied);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task TheToken_StopsAnsweringWhenExecReturns(int exitCode)
    {
        var (tool, sandbox) = Build((call, _) =>
        {
            _bridge.Find(call.Token).ShouldNotBeNull("the token answers while the command runs");
            return Task.FromResult(BridgeFixtures.Ran("", exitCode));
        });

        await tool.RunAsync("/sandbox", "true", arguments: BridgeFixtures.Whitelisted);

        _bridge.Find(sandbox.TokensSeen.ShouldHaveSingleItem()).ShouldBeNull();
    }

    [Fact]
    public async Task TheToken_StopsAnsweringWhenTheCallIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var (tool, sandbox) = Build(async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return BridgeFixtures.Ran("");
        });

        await Should.ThrowAsync<OperationCanceledException>(
            tool.RunAsync("/sandbox", "sleep 60", arguments: BridgeFixtures.Whitelisted, cancellationToken: cts.Token));

        _bridge.Find(sandbox.TokensSeen.ShouldHaveSingleItem()).ShouldBeNull();
    }

    [Fact]
    public async Task AnExecWithTheBridge_ReportsItsChanges_EvenWhenThereAreNone()
    {
        var (tool, _) = Build((_, _) => Task.FromResult(BridgeFixtures.Ran("")));

        var result = await tool.RunAsync("/sandbox", "true", arguments: BridgeFixtures.Whitelisted);

        result["vfsChanges"].ShouldNotBeNull().AsArray().ShouldBeEmpty();
    }

    // An outpost's exec has no bridge: its result is exactly what it was before the bridge existed.
    [Fact]
    public async Task AnExecOnAMountThatCannotCarryAToken_RunsWithNoBridgeAndReportsNoChanges()
    {
        var (tool, sandbox) = Build((_, _) => Task.FromResult(BridgeFixtures.Ran("")));
        var registry = BridgeFixtures.Registry((new PlainShell(), "outpost:box", ShellReach.Host));

        var result = await new VfsExecTool(registry, _bridge).RunAsync("outpost:box", "true", arguments: BridgeFixtures.Whitelisted);

        result["vfsChanges"].ShouldBeNull();
        result["exitCode"]!.GetValue<int>().ShouldBe(0);
        sandbox.TokensSeen.ShouldBeEmpty();
    }

    private sealed class PlainShell : global::Domain.Contracts.FileSystemBackendBase
    {
        public override string FilesystemName => "box";

        public override string DescribeMount => "A machine.";

        public override Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
            Task.FromResult<FsResult<FsExecResult>>(new FsResult<FsExecResult>.Ok(BridgeFixtures.Ran(path)));
    }
}