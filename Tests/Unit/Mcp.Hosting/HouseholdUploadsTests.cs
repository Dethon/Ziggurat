using Domain.DTOs;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Mcp.Hosting;

// A channel server's upload store holds every household's files in one place, so a file stored for
// a household is handed only to a read naming that household and the conversation it was stored under
// (the apps server's). A server that keeps its files for none (Sugary) has them read as they always were.
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

        (await _store.ReadBytesAsync(stored.Id, "household-a", "conversation", CancellationToken.None)).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task AFileStoredForAHousehold_IsNeverReadForAnotherOrForNone()
    {
        var stored = await _store.SaveForHouseholdAsync("household-a", "conversation", "photo.jpg", "image/jpeg", [1, 2, 3], CancellationToken.None);

        (await _store.ReadBytesAsync(stored.Id, "household-b", "conversation", CancellationToken.None)).ShouldBeNull();
        (await _store.ReadBytesAsync(stored.Id, null, "conversation", CancellationToken.None)).ShouldBeNull();
        (await _store.ReadBytesAsync(stored.Id, null, null, CancellationToken.None)).ShouldBeNull();
    }

    // The household alone is not enough: an app's images belong to the conversation its ask opened, so another
    // conversation of the same household — another app's, or another member's — is never handed them, nor a
    // read naming no conversation. The id is matched as the store spelled it, ':' included.
    [Fact]
    public async Task AFileStoredForAHousehold_IsNeverReadForAnotherConversationOfThatHousehold()
    {
        var stored = await _store.SaveForHouseholdAsync("household-a", "apps:ask-1", "photo.jpg", "image/jpeg", [1, 2, 3], CancellationToken.None);

        (await _store.ReadBytesAsync(stored.Id, "household-a", "apps:ask-1", CancellationToken.None)).ShouldBe([1, 2, 3]);
        (await _store.ReadBytesAsync(stored.Id, "household-a", "apps:ask-2", CancellationToken.None)).ShouldBeNull();
        (await _store.ReadBytesAsync(stored.Id, "household-a", null, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task AFileStoredForNone_IsReadByAnyRead()
    {
        var stored = await _store.SaveAsync("conversation", "photo.jpg", "image/jpeg", [1, 2, 3], CancellationToken.None);

        (await _store.ReadBytesAsync(stored.Id, null, null, CancellationToken.None)).ShouldBe([1, 2, 3]);
        (await _store.ReadBytesAsync(stored.Id, "household-a", "another", CancellationToken.None)).ShouldBe([1, 2, 3]);
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
        (await store.ReadBytesAsync(stored.Id, "household-a", "conversation", CancellationToken.None)).ShouldBeNull();
    }

    // A file whose message was never delivered is taken back at once, rather than kept for a retention
    // nothing will ever hydrate within; its conversation goes with it once empty, and a file beside it stays.
    [Fact]
    public async Task AFileTakenBack_IsGone_AndItsEmptyConversationWithIt()
    {
        var taken = await _store.SaveForHouseholdAsync("household-a", "conversation", "a.jpg", "image/jpeg", [1], CancellationToken.None);
        var kept = await _store.SaveForHouseholdAsync("household-a", "other", "b.jpg", "image/jpeg", [2], CancellationToken.None);

        _store.Remove([taken]);

        (await _store.ReadBytesAsync(taken.Id, "household-a", "conversation", CancellationToken.None)).ShouldBeNull();
        Directory.Exists(Path.Combine(_root, "conversation")).ShouldBeFalse();
        (await _store.ReadBytesAsync(kept.Id, "household-a", "other", CancellationToken.None)).ShouldBe([2]);
    }

    // Only a reference this store could have made is removed: one shaped to reach outside it removes nothing.
    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("conversation")]
    public async Task AReferenceNotTheStoresShape_RemovesNothing(string id)
    {
        var kept = await _store.SaveAsync("conversation", "a.jpg", "image/jpeg", [1], CancellationToken.None);

        _store.Remove([new AttachmentReference { Id = id, FileName = "a.jpg", MediaType = "image/jpeg", SizeBytes = 1 }]);

        (await _store.ReadBytesAsync(kept.Id, null, null, CancellationToken.None)).ShouldBe([1]);
    }
}