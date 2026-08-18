using Domain.Agents;
using Domain.DTOs.Metrics;
using Domain.Monitor;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.Domain;
using ChatMonitoring = Agent.App.ChatMonitoring;

namespace Tests.Unit.Agent;

// The supervising loop, driven against a real ChatMonitor whose channel dies the way a dropped MCP
// connection dies. Mocking the monitor would test the loop against a monitor that cannot fail the
// way the real one does — and the failure this file exists for is precisely what the real one does
// on its way out.
public class ChatMonitoringTests
{
    private static readonly MonitorRestartPolicy _policy = new()
    {
        InitialDelay = TimeSpan.FromSeconds(1),
        MaxDelay = TimeSpan.FromSeconds(60),
        HealthyRun = TimeSpan.FromMinutes(2)
    };

    // Jitter pinned to its top, so every delay is exactly the policy's doubling and a test can wait
    // for the timer by name.
    private static MonitorRestartSchedule Schedule() => new(_policy, () => 1);

    private sealed record Harness(
        ChatMonitoring Service,
        FakeChannelConnection Channel,
        RecordingMetricsPublisher Metrics,
        ArmedClock Clock)
    {
        public IReadOnlyList<ErrorEvent> Errors(string type) =>
            [.. Metrics.Published.OfType<ErrorEvent>().Where(e => e.ErrorType == type)];
    }

    private static Harness Build(Exception? failWith = null, bool completeStream = true)
    {
        var channel = new FakeChannelConnection { StreamFailure = failWith };
        if (failWith is null && completeStream)
        {
            channel.Complete();
        }

        var metrics = new RecordingMetricsPublisher();
        var clock = new ArmedClock();
        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
            new ChatThreadResolver(),
            metrics,
            null,
            NullLogger<ChatMonitor>.Instance);

        return new Harness(
            new ChatMonitoring(monitor, metrics, clock, NullLogger<ChatMonitoring>.Instance, Schedule()),
            channel,
            metrics,
            clock);
    }

    // The defect itself: a dependency that refuses instantly used to be retried instantly, for as
    // long as it kept refusing.
    [Fact]
    public async Task AMonitorThatKeepsFailing_IsRestartedOnADoublingDelay()
    {
        var harness = Build(failWith: new HttpRequestException("mcp connection dropped"));

        await harness.Service.StartAsync(CancellationToken.None);

        await harness.Clock.AdvancePastAsync(TimeSpan.FromSeconds(1));
        await harness.Clock.AdvancePastAsync(TimeSpan.FromSeconds(2));
        await Eventually.Until(
            () => harness.Errors(MonitorFault.RestartErrorType).Count >= 3,
            "the monitor to be restarted three times");

        await harness.Service.StopAsync(CancellationToken.None);

        var restarts = harness.Errors(MonitorFault.RestartErrorType);
        restarts[0].Message.ShouldContain("restart 1 in 1s");
        restarts[0].Message.ShouldContain("HttpRequestException: mcp connection dropped");
        restarts[1].Message.ShouldContain("restart 2 in 2s");
        restarts[2].Message.ShouldContain("restart 3 in 4s");
        restarts.ShouldAllBe(e => e.Service == "agent");
    }

    // A monitor whose channels simply ended is not an error, and it is still an ending: without a
    // wait it is the same hot loop with nothing in the log at all.
    [Fact]
    public async Task AMonitorWhoseChannelsEnded_IsAlsoPaidForWithAWait()
    {
        var harness = Build();

        await harness.Service.StartAsync(CancellationToken.None);
        await Eventually.Until(
            () => harness.Errors(MonitorFault.RestartErrorType).Count >= 1,
            "the ended stream to be reported as a restart");
        await harness.Service.StopAsync(CancellationToken.None);

        harness.Errors(MonitorFault.RestartErrorType)[0].Message.ShouldContain("its channels ended");
    }

    [Fact]
    public async Task ShutdownDuringTheWait_EndsTheServiceWithoutRestartingTheMonitor()
    {
        var harness = Build(failWith: new HttpRequestException("mcp connection dropped"));

        await harness.Service.StartAsync(CancellationToken.None);
        await harness.Clock.WaitUntilArmedAsync(TimeSpan.FromSeconds(1));
        await harness.Service.StopAsync(CancellationToken.None);

        // The wait was cut short rather than served, so nothing was restarted behind it.
        harness.Service.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
        harness.Errors(MonitorFault.RestartErrorType).Count.ShouldBe(1);
    }

    // An ordinary deploy cancels the token while the monitor is streaming. That used to be logged
    // and published as an agent error, which is a red line in the dashboard for every restart.
    [Fact]
    public async Task ShutdownWhileTheMonitorIsStreaming_ReportsNothingAtAll()
    {
        var harness = Build(completeStream: false);

        await harness.Service.StartAsync(CancellationToken.None);
        await harness.Service.StopAsync(CancellationToken.None);

        harness.Metrics.Published.OfType<ErrorEvent>().ShouldBeEmpty();
    }

    // Retrying a value the code cannot use is a hot loop dressed as resilience: the same exception,
    // at the same place, for as long as the deployment stands. It ends the host instead.
    [Fact]
    public async Task AFaultNoRestartCanClear_StopsTheHostInsteadOfRetrying()
    {
        var harness = Build(failWith: new ArgumentException("channel endpoint is not a URI"));

        await harness.Service.StartAsync(CancellationToken.None);

        var thrown = await Should.ThrowAsync<ArgumentException>(() => harness.Service.ExecuteTask!);
        thrown.Message.ShouldContain("not a URI");
        harness.Errors(MonitorFault.FatalErrorType).ShouldHaveSingleItem()
            .Message.ShouldContain("ArgumentException");
        harness.Errors(MonitorFault.RestartErrorType).ShouldBeEmpty();
    }
}