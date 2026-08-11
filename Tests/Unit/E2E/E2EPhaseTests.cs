using System.Reflection;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.E2E;

public class E2EPhaseTests
{
    [Fact]
    public async Task RunAsync_PhaseExceedsItsBudget_ThrowsNamingTheFixtureAndPhase()
    {
        var ex = await Should.ThrowAsync<TimeoutException>(() =>
            E2EPhase.RunAsync(
                "DashboardE2EFixture",
                "image build",
                TimeSpan.FromMilliseconds(50),
                ct => Task.Delay(Timeout.Infinite, ct)));

        ex.Message.ShouldContain("DashboardE2EFixture");
        ex.Message.ShouldContain("image build");
    }

    // A docker build that fails on its own (bad Dockerfile, missing base image) must keep
    // reporting that, not get rewritten into a timeout that hides the real message.
    [Fact]
    public async Task RunAsync_BodyFailsForItsOwnReason_PropagatesThatFailure()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            E2EPhase.RunAsync(
                "WebChatE2EFixture", "image build", TimeSpan.FromMinutes(1),
                _ => throw new InvalidOperationException("docker build failed for agent:latest")));

        ex.Message.ShouldBe("docker build failed for agent:latest");
    }

    // DashboardE2EFixture silently inherited the 5 minute base default while WebChatE2EFixture
    // overrode it to 15. Both build images and both start at the same instant, so the one with
    // the smaller budget gave up first while it was still queued behind the other's base-sdk
    // build. Budgets belong to the base class so no fixture can quietly get a different one.
    [Fact]
    public void EveryE2EFixture_InheritsItsBudgetsFromTheBase()
    {
        var fixtures = new[] { typeof(WebChatE2EFixture), typeof(DashboardE2EFixture) };
        var budgets = new[] { "ContainerStartupTimeout", "ImageBuildTimeout" };

        var overridden = fixtures
            .SelectMany(f => budgets.Select(b => new
            {
                Fixture = f.Name,
                Budget = b,
                Declaring = f.GetProperty(b, BindingFlags.Instance | BindingFlags.NonPublic)?.DeclaringType
            }))
            .Where(x => x.Declaring != typeof(E2EFixtureBase))
            .Select(x => $"{x.Fixture}.{x.Budget}")
            .ToList();

        overridden.ShouldBeEmpty();
    }
}