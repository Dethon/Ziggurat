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
    private readonly VfsBridge _bridge = new(new FakeTimeProvider(DateTimeOffset.UtcNow));

    private VfsExecTool Tool(FileSystemBackendBase mount, Func<VfsCall, CancellationToken, Task> script) =>
        new(BridgeFixtures.Registry(
            (new ScriptedSandbox(_bridge, async (call, ct) =>
            {
                await script(call, ct);
                return BridgeFixtures.Ran("");
            }), ScriptedSandbox.Mount, ShellReach.Contained),
            (mount, "/vault", null)), _bridge);

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