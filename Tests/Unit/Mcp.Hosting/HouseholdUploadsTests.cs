using Domain.DTOs;
using Mcp.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Mcp.Hosting;

// A channel server's upload store holds every household's files in one place, so a file stored for
// a household is handed only to a read naming that household. A store whose server cannot yet name the
// household a fetch is for (Sugary: its fetches act for nobody) stores files for none, and those are
// read as they always were.
public sealed class HouseholdUploadsTests : IDisposable
{
    private sealed record Storage(string StoragePath) : IUploadStoreSettings;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"channel-uploads-{Guid.NewGuid():N}");
    private readonly UploadStore _store;

    public HouseholdUploadsTests() =>
        _store = new UploadStore(
            new Storage(_root), new RetentionSettings(),
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<UploadStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AFileStoredForAHousehold_IsReadForThatHousehold()
    {
        var stored = await _store.SaveForHouseholdAsync("household-a", "conversation", "photo.jpg", "image/jpeg", [1, 2, 3], CancellationToken.None);

        (await _store.ReadBytesAsync(stored.Id, "household-a", CancellationToken.None)).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task AFileStoredForAHousehold_IsNeverReadForAnotherOrForNone()
    {
        var stored = await _store.SaveForHouseholdAsync("household-a", "conversation", "photo.jpg", "image/jpeg", [1, 2, 3], CancellationToken.None);

        (await _store.ReadBytesAsync(stored.Id, "household-b", CancellationToken.None)).ShouldBeNull();
        (await _store.ReadBytesAsync(stored.Id, null, CancellationToken.None)).ShouldBeNull();
        (await _store.ReadBytesAsync(stored.Id, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task AFileStoredForNone_IsReadByAnyRead()
    {
        var stored = await _store.SaveAsync("conversation", "photo.jpg", "image/jpeg", [1, 2, 3], CancellationToken.None);

        (await _store.ReadBytesAsync(stored.Id, CancellationToken.None)).ShouldBe([1, 2, 3]);
        (await _store.ReadBytesAsync(stored.Id, "household-a", CancellationToken.None)).ShouldBe([1, 2, 3]);
    }

    // Histories on a running deployment name files stored before households were kept, so a file
    // stored for none is laid out and described exactly as those were.
    [Fact]
    public async Task AFileStoredForNone_IsLaidOutAsBefore()
    {
        var stored = await _store.SaveAsync("conversation", "photo.jpg", "image/jpeg", [1, 2, 3], CancellationToken.None);

        var segments = stored.Id.Split('/');
        segments.Length.ShouldBe(2);
        segments[0].ShouldBe("conversation");
        var metadata = await File.ReadAllTextAsync(Path.Combine(_root, segments[0], segments[1], "meta.json"));
        metadata.ShouldBe(
            """{"FileName":"photo.jpg","MediaType":"image/jpeg","SizeBytes":3,"StoredAt":"2026-11-26T12:00:00+00:00"}""");
    }

    // A household's file sits where any other does, so the sweep takes it on the same window.
    [Fact]
    public async Task AFileStoredForAHousehold_IsSweptLikeAnyOther()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
        var retention = new RetentionSettings();
        var store = new UploadStore(new Storage(_root), retention, clock, NullLogger<UploadStore>.Instance);
        var stored = await store.SaveForHouseholdAsync("household-a", "conversation", "photo.jpg", "image/jpeg", [1], CancellationToken.None);

        store.Sweep().ShouldBe(0);
        clock.Advance(retention.AttachmentRetention + TimeSpan.FromDays(1));

        store.Sweep().ShouldBe(1);
        (await store.ReadBytesAsync(stored.Id, "household-a", CancellationToken.None)).ShouldBeNull();
    }
}