using Shouldly;

namespace Tests.Unit;

public class ReaperStartTests
{
    [Fact]
    public async Task AStartThatFails_RemovesWhatItLeftUnderItsNameBeforeTryingAgain()
    {
        var calls = new List<string>();
        var starts = 0;

        var started = await ReaperStart.RunAsync(
            _ => ++starts == 1 ? throw new InvalidOperationException("Could not find resource 'DockerContainer'") : Task.CompletedTask,
            _ =>
            {
                calls.Add($"remove after start {starts}");
                return Task.CompletedTask;
            },
            attempts: 3,
            CancellationToken.None);

        started.ShouldBeTrue();
        starts.ShouldBe(2);
        calls.ShouldBe(["remove after start 1"]);
    }

    // A start Docker never answers held the whole run's budget: every collection waited a minute before
    // its first test. An attempt has a span of its own, after which what it left is removed and the next
    // is made.
    [Fact]
    public async Task AStartThatHangs_IsGivenUpOnAfterItsOwnSpan_AndTriedAgain()
    {
        var calls = new List<string>();
        var starts = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var started = await ReaperStart.RunAsync(
            ct => ++starts == 1 ? Task.Delay(Timeout.InfiniteTimeSpan, ct) : Task.CompletedTask,
            _ =>
            {
                calls.Add($"remove after start {starts}");
                return Task.CompletedTask;
            },
            attempts: 3,
            CancellationToken.None,
            attemptSpan: TimeSpan.FromMilliseconds(200));

        started.ShouldBeTrue();
        starts.ShouldBe(2);
        calls.ShouldBe(["remove after start 1"]);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AReaperThatNeverStarts_IsGivenUpOnAfterItsAttempts_WithoutThrowing()
    {
        var starts = 0;
        var removals = 0;

        var started = await ReaperStart.RunAsync(
            _ =>
            {
                starts++;
                throw new InvalidOperationException("no docker");
            },
            _ =>
            {
                removals++;
                return Task.CompletedTask;
            },
            attempts: 3,
            CancellationToken.None);

        started.ShouldBeFalse();
        starts.ShouldBe(3);
        removals.ShouldBe(3);
    }

    [Fact]
    public async Task ARemovalThatFails_StillLeavesTheNextAttempt()
    {
        var starts = 0;

        var started = await ReaperStart.RunAsync(
            _ => ++starts == 1 ? throw new InvalidOperationException("first") : Task.CompletedTask,
            _ => throw new InvalidOperationException("nothing to remove"),
            attempts: 3,
            CancellationToken.None);

        started.ShouldBeTrue();
        starts.ShouldBe(2);
    }
}