using Shouldly;

namespace Tests.Eval.Fixtures;

// What bounds a full pass now that a scenario's runs go out together: ten families, each with up
// to four runs in flight, is thirty-odd stacks — thirty sandbox containers and seven servers
// apiece — on a machine that has to serve all of them. The gate is the one number that says how
// many, and it is the same number whichever family a run belongs to.
public class EvalConcurrencyTests
{
    [Fact]
    public async Task TheGate_AdmitsNoMoreRunsThanItsWidth()
    {
        var gate = new RunGate(2);
        var lease = new Lock();
        var inside = 0;
        var peak = 0;
        var released = new TaskCompletionSource();

        var runs = Enumerable.Range(0, 6).Select(_ => gate.RunAsync(async () =>
        {
            lock (lease)
            {
                inside++;
                peak = Math.Max(peak, inside);
                if (inside == 2)
                {
                    released.TrySetResult();
                }
            }

            await released.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (lease)
            {
                inside--;
            }

            return 1;
        }));

        (await Task.WhenAll(runs)).Sum().ShouldBe(6);
        peak.ShouldBe(2);
    }

    [Fact]
    public async Task ASlotIsGivenBack_EvenByARunThatThrew()
    {
        var gate = new RunGate(1);

        await Should.ThrowAsync<InvalidOperationException>(
            gate.RunAsync<int>(() => throw new InvalidOperationException("boom")));

        // A gate that leaked its only slot would hang the whole pass on the next run rather than
        // reporting the failure that caused it.
        var next = await gate.RunAsync(() => Task.FromResult(7)).WaitAsync(TimeSpan.FromSeconds(10));
        next.ShouldBe(7);
    }

    [Fact]
    public void AConfiguredWidth_IsWhatTheGateHolds() =>
        EvalConcurrency.WidthFrom("6", processors: 24).ShouldBe(6);

    [Fact]
    public void NoConfiguredWidth_ScalesWithTheMachine_WithinBounds()
    {
        // Half the cores, because a run is mostly waiting on a provider but its stack is not.
        EvalConcurrency.WidthFrom(null, processors: 24).ShouldBe(12);
        // A laptop still runs more than one at a time; a big host still stops short of the
        // provider's own rate limit.
        EvalConcurrency.WidthFrom(null, processors: 4).ShouldBe(4);
        EvalConcurrency.WidthFrom(null, processors: 128).ShouldBe(12);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("0")]
    [InlineData("-3")]
    public void AWidthThatIsNotAPositiveNumber_FallsBackToTheMachine(string configured) =>
        EvalConcurrency.WidthFrom(configured, processors: 24).ShouldBe(12);
}