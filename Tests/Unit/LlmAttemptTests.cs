using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Unit;

public class LlmAttemptTests
{
    private static readonly TimeSpan _budget = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task WithinAsync_FirstAttemptStalls_RetriesWithAFreshBudget()
    {
        var attempts = 0;

        var result = await LlmAttempt.WithinAsync(_budget, async ct =>
        {
            attempts++;
            if (attempts == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return "answered";
        });

        result.ShouldBe("answered");
        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task WithinAsync_EveryAttemptStalls_ThrowsAfterTheLastOne()
    {
        var attempts = 0;

        await Should.ThrowAsync<OperationCanceledException>(() =>
            LlmAttempt.WithinAsync<string>(_budget, async ct =>
            {
                attempts++;
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return "unreachable";
            }));

        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task WithinAsync_AttemptFailsItsAssertion_DoesNotRetry()
    {
        var attempts = 0;

        // Recorded rather than Should.ThrowAsync'd: Shouldly rethrows its own assertion type
        // instead of matching it, so the expected exception would escape the test.
        var thrown = await Record.ExceptionAsync(() =>
            LlmAttempt.WithinAsync<string>(_budget, _ =>
            {
                attempts++;
                throw new ShouldAssertException("the contract really is broken");
            }));

        thrown.ShouldBeOfType<ShouldAssertException>();
        attempts.ShouldBe(1);
    }

    [Fact]
    public async Task WithinAsync_AttemptSucceeds_RunsOnce()
    {
        var attempts = 0;

        var result = await LlmAttempt.WithinAsync(_budget, _ =>
        {
            attempts++;
            return Task.FromResult(42);
        });

        result.ShouldBe(42);
        attempts.ShouldBe(1);
    }
}