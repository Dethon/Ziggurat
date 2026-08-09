using System.Text;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Printing;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Printing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Printing.Vfs;

public class PrinterQueueFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "printfs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakePrinterClient _printer = new();
    private PrintSpool _spool = null!;
    private PrintQueueCoordinator _coordinator = null!;
    private readonly PrintQueueGate _gate = new();

    private PrinterQueueFileSystem Build(TimeSpan? regexMatchTimeout = null)
    {
        _spool = new PrintSpool(_root, _clock);
        _coordinator = new PrintQueueCoordinator(_spool, _printer, _gate, _clock,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        return new PrinterQueueFileSystem(
            _spool, _printer, _gate, "text,jpeg,pwg-raster,urf,pcl", regexMatchTimeout);
    }

    [Fact]
    public async Task StatusJson_IsReadOnly()
    {
        var fs = Build();

        var create = await fs.CreateAsync("status.json", "{}", true, true, CancellationToken.None);
        create.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var delete = await fs.DeleteAsync("status.json", CancellationToken.None);
        delete.ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var edit = await fs.EditAsync("status.json", new[] { new TextEdit("a", "b") }, CancellationToken.None);
        edit.ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task Create_QueuesText_Glob_Read_And_StatusReflectIt()
    {
        var fs = Build();

        var create = await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);
        create.ShouldBeOfType<FsResult<FsCreateResult>.Ok>().Value.Status.ShouldBe("queued");

        var glob = (await fs.GlobAsync("/", "*", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        glob.Entries.ShouldContain("/note.txt");
        glob.Entries.ShouldContain("/status.json");

        var read = (await fs.ReadAsync("note.txt", null, null, CancellationToken.None)).ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldBe("print me");

        var status = (await fs.ReadAsync("status.json", null, null, CancellationToken.None)).ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        status.Content.ShouldContain("note.txt");
        status.Content.ShouldContain("Queued");
    }

    [Fact]
    public async Task Glob_BraceExpansion_MatchesEitherExtension()
    {
        var fs = Build();
        await fs.CreateAsync("report.txt", "hello", false, true, CancellationToken.None);
        await fs.CreateAsync("notes.md", "hi", false, true, CancellationToken.None);

        var glob = (await fs.GlobAsync("/", "*.{txt,md}", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldContain("/report.txt");
        glob.Entries.ShouldContain("/notes.md");
    }

    // basePath scopes the pattern on every other mount, and the glob tool advertises that to the
    // model. The print queue is flat, so a basePath below the root matches nothing at all.
    [Fact]
    public async Task Glob_BasePathBelowTheRoot_ScopesThePatternAndMatchesNothing()
    {
        var fs = Build();
        await fs.CreateAsync("report.txt", "hello", false, true, CancellationToken.None);

        var glob = (await fs.GlobAsync("/subdir", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldBeEmpty();
    }

    // A trailing slash asks for directories only. The print queue has none, so the answer is empty
    // rather than the file list the pattern would otherwise produce.
    [Fact]
    public async Task Glob_TrailingSlash_ReturnsDirectoriesOnly()
    {
        var fs = Build();
        await fs.CreateAsync("report.txt", "hello", false, true, CancellationToken.None);

        var glob = (await fs.GlobAsync("/", "*/", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_DuplicateName_RequiresOverwrite()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "v1", false, true, CancellationToken.None);

        var dup = await fs.CreateAsync("note.txt", "v2", false, true, CancellationToken.None);
        dup.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode.ShouldBe("already_exists");

        var ok = await fs.CreateAsync("note.txt", "v2", true, true, CancellationToken.None);
        ok.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        (await fs.ReadAsync("note.txt", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldBe("v2");
    }

    [Fact]
    public async Task Delete_BeforeSubmit_DoesNotPrint()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);

        var delete = await fs.DeleteAsync("note.txt", CancellationToken.None);
        delete.ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();

        _printer.Submissions.ShouldBeEmpty();
        _printer.Canceled.ShouldBeEmpty();
        (await fs.GlobAsync("/", "*", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>()
            .Value.Entries.ShouldNotContain("/note.txt");
    }

    [Fact]
    public async Task Delete_AfterSubmit_CancelsActiveJob()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await _coordinator.SubmitDueAsync(CancellationToken.None);
        var jobId = (await _spool.GetAsync("note.txt", CancellationToken.None))!.JobId!.Value;

        var delete = await fs.DeleteAsync("note.txt", CancellationToken.None);
        delete.ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();
        _printer.Canceled.ShouldContain(jobId);
    }

    [Fact]
    public async Task ReadAndInfo_BinaryDocument_AreHandled()
    {
        var fs = Build();
        // A supported binary format (JPEG) — read-as-text still fails, info still works.
        await fs.WriteChunksAsync("scan.jpg", Single(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }), true, true, CancellationToken.None);

        var read = await fs.ReadAsync("scan.jpg", null, null, CancellationToken.None);
        read.ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var info = (await fs.InfoAsync("scan.jpg", CancellationToken.None)).ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        info.Exists.ShouldBeTrue();
        info.Size.ShouldBe(6);
    }

    [Fact]
    public async Task WriteChunks_UnsupportedFormat_IsRejected()
    {
        var fs = Build();
        // PDF is not in the supported set; the backend rejects it rather than spooling unprintable bytes.
        await Should.ThrowAsync<InvalidOperationException>(
            fs.WriteChunksAsync("scan.pdf", Single(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0x01 }), true, true, CancellationToken.None));
    }

    [Fact]
    public async Task Edit_ReplacesText_AndCancelsPriorSubmission()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "hello world", false, true, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await _coordinator.SubmitDueAsync(CancellationToken.None);
        var jobId = (await _spool.GetAsync("note.txt", CancellationToken.None))!.JobId!.Value;

        var edit = await fs.EditAsync("note.txt", new[] { new TextEdit("world", "there") }, CancellationToken.None);
        edit.ShouldBeOfType<FsResult<FsEditResult>.Ok>().Value.TotalOccurrencesReplaced.ShouldBe(1);

        _printer.Canceled.ShouldContain(jobId);
        (await fs.ReadAsync("note.txt", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldBe("hello there");
        (await _spool.GetAsync("note.txt", CancellationToken.None))!.IsSubmitted.ShouldBeFalse();
    }

    [Fact]
    public async Task Copy_DuplicatesDocumentAsNewQueueEntry()
    {
        var fs = Build();
        await fs.CreateAsync("a.txt", "content", false, true, CancellationToken.None);

        var copy = await fs.CopyAsync("a.txt", "b.txt", false, true, CancellationToken.None);
        copy.ShouldBeOfType<FsResult<FsCopyResult>.Ok>();

        (await fs.ReadAsync("b.txt", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldBe("content");
    }

    [Fact]
    public async Task Search_FindsTextAcrossQueuedDocuments()
    {
        var fs = Build();
        await fs.CreateAsync("a.txt", "the quick brown fox", false, true, CancellationToken.None);
        await fs.CreateAsync("b.txt", "lazy dog", false, true, CancellationToken.None);

        var search = (await fs.SearchAsync("quick", false, null, null, "*", 50, 0, VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.FilesWithMatches.ShouldBe(1);
        search.Results[0].File.ShouldBe("/a.txt");
    }

    // filePattern is the second caller-supplied pattern in a search, and it was compiled with no
    // match timeout at all — on this mount the names it runs against are the caller's own file
    // names, so `*a*a*a…b` over a long one backtracks past any deadline and stalls the turn. It
    // answers the same timeout envelope the query already does.
    [Fact]
    public async Task Search_PathologicalFilePattern_ReturnsTimeoutEnvelope()
    {
        var fs = Build(TimeSpan.FromMilliseconds(1));
        await fs.CreateAsync(new string('a', 30) + ".txt", "content", false, true, CancellationToken.None);

        var search = await fs.SearchAsync(
            "content", false, null, null, string.Concat(Enumerable.Repeat("*a", 12)) + "b",
            50, 0, VfsTextSearchOutputMode.Content, CancellationToken.None);

        search.ShouldBeOfType<FsResult<FsSearchResult>.Err>().Error.ErrorCode.ShouldBe("timeout");
    }

    [Fact]
    public async Task Reads_DoNotPruneFinishedJobs_OnlyTheCoordinatorDoes()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await _coordinator.SubmitDueAsync(CancellationToken.None);
        var jobId = (await _spool.GetAsync("note.txt", CancellationToken.None))!.JobId!.Value;

        _printer.CompleteJob(jobId);

        // Record the absence and let the grace window elapse so the job is now prune-eligible.
        await _coordinator.ReconcileAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(600));

        // Reads are side-effect free: globbing/reading/info must NOT prune the finished job.
        (await fs.GlobAsync("/", "*", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>()
            .Value.Entries.ShouldContain("/note.txt");
        await fs.ReadAsync("status.json", null, null, CancellationToken.None);
        await fs.InfoAsync("note.txt", CancellationToken.None);
        (await _spool.GetAsync("note.txt", CancellationToken.None)).ShouldNotBeNull();

        // The background coordinator reconcile is what actually prunes it.
        await _coordinator.ReconcileAsync(CancellationToken.None);
        (await _spool.GetAsync("note.txt", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Tick_WaitsForSharedGate_BeforeSubmitting()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(600)); // now past the submit debounce

        var holder = await _gate.AcquireAsync(CancellationToken.None);
        var tick = _coordinator.TickAsync(CancellationToken.None);
        await Task.Delay(100);
        tick.IsCompleted.ShouldBeFalse();      // blocked on the shared gate
        _printer.Submissions.ShouldBeEmpty();   // nothing submitted while a foreground op holds the gate

        holder.Dispose();
        await tick;
        _printer.Submissions.ShouldContain(s => s.JobName == "note.txt"); // proceeds once released
    }

    [Fact]
    public async Task Delete_WaitsForSharedGate()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await _coordinator.SubmitDueAsync(CancellationToken.None);
        var jobId = (await _spool.GetAsync("note.txt", CancellationToken.None))!.JobId!.Value;

        var holder = await _gate.AcquireAsync(CancellationToken.None);
        var delete = fs.DeleteAsync("note.txt", CancellationToken.None);
        await Task.Delay(100);
        delete.IsCompleted.ShouldBeFalse();    // blocked on the shared gate
        _printer.Canceled.ShouldBeEmpty();      // no cancellation while the gate is held

        holder.Dispose();
        (await delete).ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();
        _printer.Canceled.ShouldContain(jobId); // cancels once it can take the gate
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Single(byte[] bytes)
    {
        yield return bytes;
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}