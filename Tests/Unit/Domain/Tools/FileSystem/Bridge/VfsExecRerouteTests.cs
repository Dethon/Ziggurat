using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem.Bridge;

// exec runs where the files are: on a mount with no shell of its own (timers, schedules, Home
// Assistant) it runs in the sandbox with that mount as the working directory when the session has
// one, and on the mount's own catalog when it does not — so `./dismiss` works for every agent.
public class VfsExecRerouteTests
{
    private readonly VfsBridge _bridge = new(new FakeTimeProvider(DateTimeOffset.UtcNow));
    private readonly CatalogMount _timers = new();
    private readonly List<(string Path, string Command)> _sandboxRan = [];

    private IVirtualFileSystemRegistry WithSandbox() => BridgeFixtures.Registry(
        (new RecordingSandbox(_bridge, _sandboxRan), ScriptedSandbox.Mount, ShellReach.Contained),
        (_timers, "/timers", null));

    [Fact]
    public async Task ExecOnAMountWithNoShell_RunsInTheSandboxThere()
    {
        var result = await new VfsExecTool(WithSandbox(), _bridge)
            .RunAsync("/timers", "./dismiss", arguments: BridgeFixtures.Whitelisted);

        _sandboxRan.ShouldBe([("vfs/timers", "./dismiss")]);
        _timers.Ran.ShouldBeEmpty();
        result["cwd"]!.GetValue<string>().ShouldBe("/timers");
        result["vfsChanges"].ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecDeeperInAMountWithNoShell_RunsInThatDirectory()
    {
        var result = await new VfsExecTool(WithSandbox(), _bridge)
            .RunAsync("/timers/eggs", "ls", arguments: BridgeFixtures.Whitelisted);

        _sandboxRan.ShouldBe([("vfs/timers/eggs", "ls")]);
        result["cwd"]!.GetValue<string>().ShouldBe("/timers/eggs");
    }

    // jack has no sandbox: the same call runs the mount's own catalog, with the same effect.
    [Fact]
    public async Task ExecWithNoSandbox_RunsTheMountsOwnCatalog()
    {
        var registry = BridgeFixtures.Registry((_timers, "/timers", null));

        await new VfsExecTool(registry, _bridge).RunAsync("/timers", "./dismiss", arguments: BridgeFixtures.Whitelisted);

        _timers.Ran.ShouldBe([("", "./dismiss")]);
    }

    // A host with no bridge cannot show the sandbox the mount, so the catalog answers.
    [Fact]
    public async Task ExecWithNoBridge_RunsTheMountsOwnCatalog()
    {
        await new VfsExecTool(WithSandbox()).RunAsync("/timers", "./dismiss", arguments: BridgeFixtures.Whitelisted);

        _timers.Ran.ShouldBe([("", "./dismiss")]);
        _sandboxRan.ShouldBeEmpty();
    }

    // The screen judges where the command really runs: a rerouted exec takes the sandbox's reach,
    // an outpost's is still somebody's own computer.
    [Fact]
    public void TheReachOfARoutedExec_IsTheSandboxs()
    {
        var registry = BridgeFixtures.Registry(
            (new RecordingSandbox(_bridge, _sandboxRan), ScriptedSandbox.Mount, ShellReach.Contained),
            (_timers, "/timers", null),
            (new CatalogMount(), "outpost:box", ShellReach.Host));

        var reach = ExecReach.Over(registry, reroutes: true);

        reach.Of("/timers").ShouldBe(ShellReach.Contained);
        reach.Of("/sandbox/home").ShouldBe(ShellReach.Contained);
        reach.Of("outpost:box/home").ShouldBe(ShellReach.Host);
        ExecReach.Over(registry, reroutes: false).Of("/timers").ShouldBeNull();
        ExecReach.Over(BridgeFixtures.Registry((_timers, "/timers", null)), reroutes: true).Of("/timers").ShouldBeNull();
    }

    private sealed class RecordingSandbox(VfsBridge bridge, List<(string, string)> ran)
        : FileSystemBackendBase, IBridgedExecBackend
    {
        public override string FilesystemName => "sandbox";

        public override string DescribeMount => "Sandbox.";

        public override ShellReach? ShellReach => global::Domain.DTOs.ShellReach.Contained;

        public override Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
            Task.FromResult<FsResult<FsExecResult>>(new FsResult<FsExecResult>.Ok(BridgeFixtures.Ran(path)));

        public Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, VfsBridgeGrant grant, CancellationToken ct)
        {
            bridge.Find(grant.Token).ShouldNotBeNull();
            ran.Add((path, command));
            return Task.FromResult<FsResult<FsExecResult>>(new FsResult<FsExecResult>.Ok(BridgeFixtures.Ran(path)));
        }
    }

    private sealed class CatalogMount : FileSystemBackendBase
    {
        public List<(string Path, string Command)> Ran { get; } = [];

        public override string FilesystemName => "timers";

        public override string DescribeMount => "Timers.";

        public override Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
        {
            Ran.Add((path, command));
            return Task.FromResult<FsResult<FsExecResult>>(new FsResult<FsExecResult>.Ok(BridgeFixtures.Ran(path)));
        }
    }
}