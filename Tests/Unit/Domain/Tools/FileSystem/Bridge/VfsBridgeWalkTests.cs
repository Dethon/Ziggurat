using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem.Bridge;

// A mount's walk budget bounds what a listing can return, and a `grep -r` that saw only part of a
// directory must say so: the exec result names every directory whose listing stopped early.
public class VfsBridgeWalkTests
{
    private readonly VfsBridge _bridge = new(new FakeTimeProvider(DateTimeOffset.UtcNow), new VfsBridgeSettings());

    private VfsExecTool Tool(FileSystemBackendBase mount, Func<VfsCall, CancellationToken, Task> script) =>
        new(BridgeFixtures.Registry(
            (new ScriptedSandbox(_bridge, async (call, ct) =>
            {
                await script(call, ct);
                return BridgeFixtures.Ran("");
            }), ScriptedSandbox.Mount, ShellReach.Contained),
            (mount, "/vault", null)), _bridge);

    // The daemon asks for the working directory once, before the command runs: every directory on
    // the way to it and its listing, in one request — on prod each question was a round trip out
    // through the home's tunnel and back, asked one after another by the kernel.
    [Fact]
    public async Task AWalk_AnswersEveryStepOnTheWayAndTheListing()
    {
        BridgeWalk? walk = null;
        await Tool(new MemoryDisk("vault", new Dictionary<string, string>
        {
            ["notes/deep/todo.md"] = "x",
            ["notes/deep/done.md"] = "y"
        }), async (call, ct) => walk = (await call.WalkAsync("/vault/notes/deep", ct))
            .ShouldBeOfType<BridgeAnswer<BridgeWalk>.Ok>().Value)
            .RunAsync("/sandbox", "ls", arguments: BridgeFixtures.Whitelisted);

        walk!.Steps.Select(s => (s.Path, s.Attr.Kind)).ShouldBe([
            ("/vault", BridgeKinds.Directory),
            ("/vault/notes", BridgeKinds.Directory),
            ("/vault/notes/deep", BridgeKinds.Directory)
        ]);
        walk.Listing!.Entries.Select(e => e.Name).Order().ShouldBe(["done.md", "todo.md"]);
    }

    // A walk is a prefetch, never an answer the kernel acts on: it stops at the first step that is
    // not there or not allowed, lists nothing that is not a directory it reached, and each step is
    // gated as the attr it stands for.
    [Theory]
    [InlineData("/vault/nope/deeper", true, new[] { "/vault" })]
    [InlineData("/vault/notes/todo.md", true, new[] { "/vault", "/vault/notes", "/vault/notes/todo.md" })]
    [InlineData("/vault/notes", false, new[] { "/vault" })]
    public async Task AWalk_StopsWhereTheAttrWould(string path, bool infoRunsUnasked, string[] steps)
    {
        BridgeWalk? walk = null;
        var arguments = infoRunsUnasked ? BridgeFixtures.Whitelisted : BridgeFixtures.Allowing(VfsGlobFilesTool.Name, VfsExecTool.Name);
        await Tool(new MemoryDisk("vault", new Dictionary<string, string> { ["notes/todo.md"] = "x" }),
                async (call, ct) => walk = (await call.WalkAsync(path, ct)).ShouldBeOfType<BridgeAnswer<BridgeWalk>.Ok>().Value)
            .RunAsync("/sandbox", "ls", arguments: arguments);

        walk!.Steps.Select(s => s.Path).ShouldBe(steps);
        walk.Listing.ShouldBeNull();
    }

    [Fact]
    public async Task AListingTheMountCutShort_IsReportedOnTheExecResult()
    {
        var result = await Tool(new BudgetedMount(), async (call, ct) =>
        {
            (await call.ListAsync("/vault/big", ct)).ShouldBeOfType<BridgeAnswer<BridgeListing>.Ok>().Value.Truncated.ShouldBeTrue();
            await call.ListAsync("/vault/big", ct);
            await call.ListAsync("/vault/small", ct);
        }).RunAsync("/sandbox", "grep -r TODO /vault", arguments: BridgeFixtures.Whitelisted);

        result["vfsTruncated"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["/vault/big"]);
    }

    [Fact]
    public async Task AWalkThatSawEverything_ReportsNothingTruncated()
    {
        var result = await Tool(new BudgetedMount(), (call, ct) => call.ListAsync("/vault/small", ct))
            .RunAsync("/sandbox", "ls /vault/small", arguments: BridgeFixtures.Whitelisted);

        result["vfsTruncated"].ShouldBeNull();
    }

    // A filesystem sharing the mount point that did not answer may hold the name a command asks
    // for, so the listing is short rather than complete: the daemon must not answer ENOENT from it.
    [Fact]
    public async Task AListingAMemberDidNotAnswer_IsCutShort()
    {
        var result = await Tool(new PartlyAnsweringMount(), async (call, ct) =>
                (await call.ListAsync("/vault", ct)).ShouldBeOfType<BridgeAnswer<BridgeListing>.Ok>().Value.Truncated.ShouldBeTrue())
            .RunAsync("/sandbox", "ls /vault", arguments: BridgeFixtures.Whitelisted);

        result["vfsTruncated"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["/vault"]);
    }

    private sealed class PartlyAnsweringMount : FileSystemBackendBase
    {
        public override string FilesystemName => "vault";

        public override string DescribeMount => "A shared mount point one member of which is away.";

        public override Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct) =>
            Task.FromResult<FsResult<FsGlobResult>>(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = ["porch/"],
                Truncated = false,
                Total = 1,
                Unavailable = ["sugary-watches"]
            }));
    }

    private sealed class BudgetedMount : FileSystemBackendBase
    {
        public override string FilesystemName => "vault";

        public override string DescribeMount => "A mount with a walk budget.";

        public override Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct) =>
            Task.FromResult<FsResult<FsGlobResult>>(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = ["x.md"],
                Truncated = false,
                Total = 1,
                BudgetReached = basePath.Contains("big"),
                EntriesScanned = 50_000
            }));
    }
}