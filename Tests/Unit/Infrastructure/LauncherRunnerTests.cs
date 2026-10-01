using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Infrastructure.Clients.Bash;
using Shouldly;
using Xunit;

namespace Tests.Unit.Infrastructure;

// The server's half of the launcher protocol, against a launcher played by the test: one request
// line in, one answer line out. The launcher itself (sandbox-runtime/) is exercised in the real
// image by the sandbox E2E suite.
public class LauncherRunnerTests : IDisposable
{
    private readonly string _socket = Path.Combine(Path.GetTempPath(), $"launcher-{Guid.NewGuid():N}.sock");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"launcher-root-{Guid.NewGuid():N}");
    private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LauncherRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "work"));
    }

    private LauncherRunner Runner(IReadOnlyDictionary<string, string>? environment = null) => new(
        new BashRunnerOptions
        {
            ContainerRoot = _root,
            DefaultTimeoutSeconds = 7,
            MaxTimeoutSeconds = 30,
            OutputCapBytes = 512,
            Environment = environment ?? new Dictionary<string, string> { ["HOME"] = "/home/sandbox_user" }
        },
        _socket);

    private static void SkipIfNotLinux() =>
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), "Unix sockets as the launcher uses them");

    // Accepts one connection, records the request line and answers with `answer` (null: hang up).
    private Task<string> PlayLauncherAsync(string? answer, TaskCompletionSource? hungUp = null)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(_socket));
        listener.Listen(1);
        return Task.Run(async () =>
        {
            using (listener)
            using (var connection = await listener.AcceptAsync())
            await using (var stream = new NetworkStream(connection))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var request = await reader.ReadLineAsync() ?? "";
                _requested.TrySetResult();
                if (answer is not null)
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(answer + "\n"));
                }
                else if (hungUp is not null)
                {
                    // Waits for the runner to close its end, which is what a cancellation is.
                    var buffer = new byte[1];
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0)
                    {
                        hungUp.TrySetResult();
                    }
                }

                return request;
            }
        });
    }

    [SkippableFact]
    public async Task RunAsync_SendsTheJailedCwdTheClampedTimeoutTheCapAndTheEnvironment()
    {
        SkipIfNotLinux();
        var launcher = PlayLauncherAsync(
            """{"stdout":"hi\n","stderr":"","exitCode":0,"timedOut":false,"truncated":false,"durationMs":4}""");

        await Runner().RunAsync("/work", "echo hi", 999, CancellationToken.None);

        var request = JsonNode.Parse(await launcher)!;
        request["command"]!.GetValue<string>().ShouldBe("echo hi");
        request["cwd"]!.GetValue<string>().ShouldBe(Path.Combine(_root, "work"));
        request["timeoutSeconds"]!.GetValue<int>().ShouldBe(30);
        request["outputCapBytes"]!.GetValue<int>().ShouldBe(512);
        request["env"]!["HOME"]!.GetValue<string>().ShouldBe("/home/sandbox_user");
    }

    [SkippableFact]
    public async Task RunAsync_NoTimeout_SendsTheDefault()
    {
        SkipIfNotLinux();
        var launcher = PlayLauncherAsync(
            """{"stdout":"","stderr":"","exitCode":0,"timedOut":false,"truncated":false,"durationMs":1}""");

        await Runner().RunAsync("", "true", null, CancellationToken.None);

        JsonNode.Parse(await launcher)!["timeoutSeconds"]!.GetValue<int>().ShouldBe(7);
    }

    [SkippableFact]
    public async Task RunAsync_TheAnswer_IsTheExecResultWithACwdRelativeToTheRoot()
    {
        SkipIfNotLinux();
        _ = PlayLauncherAsync(
            """{"stdout":"out","stderr":"err","exitCode":3,"timedOut":false,"truncated":true,"durationMs":12}""");

        var result = (await Runner().RunAsync("work", "x", null, CancellationToken.None)).ToNode();

        result["stdout"]!.GetValue<string>().ShouldBe("out");
        result["stderr"]!.GetValue<string>().ShouldBe("err");
        result["exitCode"]!.GetValue<int>().ShouldBe(3);
        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
        result["durationMs"]!.GetValue<long>().ShouldBe(12);
        result["cwd"]!.GetValue<string>().ShouldBe("work");
    }

    [SkippableFact]
    public async Task RunAsync_ATimeout_IsReportedAsBefore()
    {
        SkipIfNotLinux();
        _ = PlayLauncherAsync(
            """{"stdout":"","stderr":"","exitCode":-1,"timedOut":true,"truncated":false,"durationMs":1000}""");

        var result = (await Runner().RunAsync("", "sleep 9", 1, CancellationToken.None)).ToNode();

        result["timedOut"]!.GetValue<bool>().ShouldBeTrue();
        result["exitCode"]!.GetValue<int>().ShouldBe(-1);
    }

    [SkippableFact]
    public async Task RunAsync_ALauncherThatCouldNotRunIt_IsAnErrorEnvelope()
    {
        SkipIfNotLinux();
        _ = PlayLauncherAsync("""{"error":"cannot isolate the command: Operation not permitted"}""");

        var result = (await Runner().RunAsync("", "true", null, CancellationToken.None)).ToNode();

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["message"]!.GetValue<string>().ShouldContain("cannot isolate the command");
    }

    // Hanging up is the cancellation: the launcher kills the tree when its connection closes.
    [SkippableFact]
    public async Task RunAsync_Cancelled_HangsUpOnTheLauncher()
    {
        SkipIfNotLinux();
        var hungUp = new TaskCompletionSource();
        _ = PlayLauncherAsync(null, hungUp);
        using var cts = new CancellationTokenSource();

        var run = Runner().RunAsync("", "sleep 60", null, cts.Token);
        await _requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(run);
        await hungUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [SkippableFact]
    public async Task RunAsync_PathClimbingOutOfTheRoot_IsRefusedWithoutAskingTheLauncher()
    {
        SkipIfNotLinux();

        var result = (await Runner().RunAsync("../../etc", "pwd", null, CancellationToken.None)).ToNode();

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_socket);
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A leftover temp entry is not a failing test.
        }
    }
}