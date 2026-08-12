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

    [Theory]
    [InlineData(4)]
    public void NextRetryDelay_PastTheSchedule_SettlesAtThirtySeconds(long previousRetryCount)
    {
        DelayAfter(previousRetryCount, TimeSpan.FromHours(previousRetryCount))
            .ShouldBe(TimeSpan.FromSeconds(30));
    }
}