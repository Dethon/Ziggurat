using Dashboard.Client.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.Services;

public class MetricsRetryPolicyTests
{
    private static readonly MetricsRetryPolicy _policy = new();

    private static TimeSpan? DelayAfter(long previousRetryCount, TimeSpan elapsed) =>
        _policy.NextRetryDelay(new RetryContext
        {
            PreviousRetryCount = previousRetryCount,
            ElapsedTime = elapsed,
            RetryReason = new InvalidOperationException("hub unavailable"),
        });

    public static TheoryData<long, int> ScheduledDelays => new()
    {
        { 0, 0 },
        { 1, 2 },
        { 2, 10 },
        { 3, 30 },
    };

    [Theory]
    [MemberData(nameof(ScheduledDelays))]
    public void NextRetryDelay_WithinTheSchedule_IsTheScheduledDelay(long previousRetryCount, int expectedSeconds)
    {
        DelayAfter(previousRetryCount, TimeSpan.FromSeconds(previousRetryCount))
            .ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(100_000)]
    public void NextRetryDelay_PastTheSchedule_SettlesAtThirtySeconds(long previousRetryCount)
    {
        DelayAfter(previousRetryCount, TimeSpan.FromHours(previousRetryCount))
            .ShouldBe(TimeSpan.FromSeconds(30));
    }
}