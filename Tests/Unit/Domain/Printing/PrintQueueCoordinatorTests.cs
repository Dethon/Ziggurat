using System.Text;
using Domain.Tools.Printing;
using Infrastructure.Printing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Printing;

public class PrintQueueCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "printcoord-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakePrinterClient _printer = new();

    private (PrintSpool Spool, PrintQueueCoordinator Coordinator) Build()
    {
        var spool = new PrintSpool(_root, _clock);
        return (spool, new PrintQueueCoordinator(spool, _printer, new PrintQueueGate(), _clock,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task SubmitDue_WaitsForDebounce_ThenSubmits()
    {
        var (spool, coordinator) = Build();
        await spool.WriteBytesAsync("a.txt", "text/plain", Encoding.UTF8.GetBytes("hi"), 0, true, CancellationToken.None);

        // Too soon — still inside the debounce window.
        await coordinator.SubmitDueAsync(CancellationToken.None);
        _printer.Submissions.ShouldBeEmpty();
        (await spool.GetAsync("a.txt", CancellationToken.None))!.IsSubmitted.ShouldBeFalse();

        // After the window the document is submitted exactly once.
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await coordinator.SubmitDueAsync(CancellationToken.None);
        _printer.Submissions.Count.ShouldBe(1);
        _printer.Submissions[0].JobName.ShouldBe("a.txt");

        var entry = await spool.GetAsync("a.txt", CancellationToken.None);
        entry!.IsSubmitted.ShouldBeTrue();

        // Idempotent — a second pass does not resubmit.
        await coordinator.SubmitDueAsync(CancellationToken.None);
        _printer.Submissions.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Reconcile_PrunesFinishedJobs_KeepsActiveOnes()
    {
        var (spool, coordinator) = Build();
        await spool.WriteBytesAsync("a.txt", "text/plain", Encoding.UTF8.GetBytes("hi"), 0, true, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await coordinator.SubmitDueAsync(CancellationToken.None);

        var jobId = (await spool.GetAsync("a.txt", CancellationToken.None))!.JobId!.Value;

        // Still printing → kept.
        await coordinator.ReconcileAsync(CancellationToken.None);
        (await spool.GetAsync("a.txt", CancellationToken.None)).ShouldNotBeNull();

        // Printer finished it → not pruned on first sight (debounced), only once absent past the grace.
        _printer.CompleteJob(jobId);
        await coordinator.ReconcileAsync(CancellationToken.None);
        (await spool.GetAsync("a.txt", CancellationToken.None)).ShouldNotBeNull();

        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await coordinator.ReconcileAsync(CancellationToken.None);
        (await spool.GetAsync("a.txt", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Reconcile_TransientAbsence_DoesNotPrune_AndReappearanceClearsTheMark()
    {
        var (spool, coordinator) = Build();
        await spool.WriteBytesAsync("a.txt", "text/plain", Encoding.UTF8.GetBytes("hi"), 0, true, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await coordinator.SubmitDueAsync(CancellationToken.None);
        var jobId = (await spool.GetAsync("a.txt", CancellationToken.None))!.JobId!.Value;

        // A transient empty/partial Get-Jobs response — the job briefly vanishes from the active set.
        _printer.CompleteJob(jobId);
        await coordinator.ReconcileAsync(CancellationToken.None);
        (await spool.GetAsync("a.txt", CancellationToken.None))!.MissingSince.ShouldNotBeNull();

        // It reappears on the next poll (still printing); the missing mark is cleared, nothing pruned.
        _printer.SetActive(jobId, "a.txt");
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await coordinator.ReconcileAsync(CancellationToken.None);

        var entry = await spool.GetAsync("a.txt", CancellationToken.None);
        entry.ShouldNotBeNull();
        entry!.MissingSince.ShouldBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}