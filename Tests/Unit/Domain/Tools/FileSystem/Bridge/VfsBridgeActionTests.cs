using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem.Bridge;

// An action file run from a script reaches the bridge as the exec tool's call on the action's
// directory: `./<name>` with the script's arguments, answered with the action's real output and
// exit code, and listed among the command's changes.
public class VfsBridgeActionTests
{
    private readonly VfsBridge _bridge = new(new FakeTimeProvider(DateTimeOffset.UtcNow));

    private readonly ActionMount _timers = new();

    private async Task<(BridgeAnswer<BridgeActionResult>? Answer, System.Text.Json.Nodes.JsonNode Result)> RunAsync(
        string path, IReadOnlyList<string> argv, AIFunctionArgumentsWithPermission? arguments = null)
    {
        BridgeAnswer<BridgeActionResult>? answer = null;
        var tool = new VfsExecTool(BridgeFixtures.Registry(
            (new ScriptedSandbox(_bridge, async (call, ct) =>
            {
                answer = await call.ActionAsync(path, argv, ct);
                return BridgeFixtures.Ran("");
            }), ScriptedSandbox.Mount, ShellReach.Contained),
            (_timers, "/timers", null)), _bridge);
        var result = await tool.RunAsync("/sandbox", "script", arguments: arguments ?? BridgeFixtures.Whitelisted);
        return (answer, result);
    }

    [Fact]
    public async Task AnAction_IsTheMountsExecOnItsDirectory_WithItsRealOutputAndExitCode()
    {
        var (answer, result) = await RunAsync("/timers/dismiss", []);

        _timers.Ran.ShouldBe([("", "./dismiss")]);
        answer.ShouldBeOfType<BridgeAnswer<BridgeActionResult>.Ok>().Value
            .ShouldBe(new BridgeActionResult("dismissed 1\n", "", 0));
        var change = result["vfsChanges"]!.AsArray().ShouldHaveSingleItem()!;
        (change["path"]!.GetValue<string>(), change["operation"]!.GetValue<string>(), change["status"]!.GetValue<string>())
            .ShouldBe(("/timers/dismiss", VfsChange.Operations.Action, VfsChange.Statuses.Applied));
    }

    // The script's arguments arrive as the words they were, however they are spelled.
    [Fact]
    public async Task ArgumentsArriveAsTheWordsTheyWere()
    {
        await RunAsync("/timers/eggs/snooze", ["--minutes", "5", "it's late"]);

        _timers.Ran.ShouldBe([("eggs", "./snooze --minutes 5 'it'\\''s late'")]);
    }

    [Fact]
    public async Task AFailingAction_PassesItsExitCodeThrough()
    {
        var (answer, _) = await RunAsync("/timers/fail", []);

        answer.ShouldBeOfType<BridgeAnswer<BridgeActionResult>.Ok>().Value.ExitCode.ShouldBe(3);
    }

    // An exec on the mount that would ask the person is refused rather than run.
    [Fact]
    public async Task AnActionTheExecToolWouldAskAbout_IsRefused()
    {
        var (answer, result) = await RunAsync("/timers/dismiss", [],
            BridgeFixtures.Allowing(FileSystemToolFeature.Callable(VfsFileReadTool.Name)));

        answer.ShouldBeOfType<BridgeAnswer<BridgeActionResult>.Refused>().Errno.ShouldBe(Errnos.Denied);
        _timers.Ran.ShouldBeEmpty();
        result["vfsChanges"]![0]!["status"]!.GetValue<string>().ShouldBe(VfsChange.Statuses.Refused);
    }

    private sealed class ActionMount : global::Domain.Contracts.FileSystemBackendBase
    {
        public List<(string Path, string Command)> Ran { get; } = [];

        public override string FilesystemName => "timers";

        public override string DescribeMount => "Actions.";

        public override Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
        {
            Ran.Add((path, command));
            return Task.FromResult<FsResult<FsExecResult>>(new FsResult<FsExecResult>.Ok(
                BridgeFixtures.Ran(path, command == "./fail" ? 3 : 0) with
                {
                    Stdout = command == "./dismiss" ? "dismissed 1\n" : ""
                }));
        }
    }
}