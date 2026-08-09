using Domain.DTOs.Channel;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

// Files do not accumulate forever. Deleting a topic takes its files with it; everything topic
// deletion never reaches — conversations nobody deletes, uploads for a message that was
// abandoned before it was sent — is collected by the sweep. A swept file's reference still reads
// back as nothing rather than an error, because the placeholder path is ordinary behaviour.
public sealed class AttachmentRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"attachments-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly AttachmentSettings _settings;
    private readonly AttachmentStore _store;

    public AttachmentRetentionTests()
    {
        _settings = new AttachmentSettings { StoragePath = _root, RetentionDays = 30 };
        _store = new AttachmentStore(_settings, _time, NullLogger<AttachmentStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task DeletingAConversation_RemovesTheFilesSentInIt()
    {
        var mine = await StoreAsync("7:42", "photo.png");
        var elsewhere = await StoreAsync("8:99", "other.png");

        _store.DeleteConversation("7:42");

        _store.Find(mine.Id).ShouldBeNull();
        _store.Find(elsewhere.Id).ShouldNotBeNull();
    }

    [Fact]
    public async Task TheSweep_RemovesFilesOlderThanTheRetentionWindow()
    {
        var old = await StoreAsync("7:42", "old.png");
        _time.Advance(TimeSpan.FromDays(_settings.RetentionDays + 1));
        var fresh = await StoreAsync("7:42", "fresh.png");

        _store.Sweep().ShouldBe(1);

        _store.Find(old.Id).ShouldBeNull();
        _store.Find(fresh.Id).ShouldNotBeNull();
    }

    [Fact]
    public async Task ASweptFilesReference_ReadsBackAsNothingRatherThanAnError()
    {
        var swept = await StoreAsync("7:42", "gone.png");
        _time.Advance(TimeSpan.FromDays(_settings.RetentionDays + 1));
        _store.Sweep();

        (await _store.ReadBytesAsync(swept.Id, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task TwoFilesWithTheSameNameInOneConversation_BothSurvive()
    {
        var first = await StoreAsync("7:42", "scan.pdf");
        var second = await StoreAsync("7:42", "scan.pdf");

        first.Id.ShouldNotBe(second.Id);
        _store.Find(first.Id).ShouldNotBeNull();
        _store.Find(second.Id).ShouldNotBeNull();
    }

    private async Task<AttachmentReference> StoreAsync(string conversationId, string fileName)
    {
        using var content = new MemoryStream("bytes"u8.ToArray());
        return await _store.SaveAsync(
            conversationId, "default", fileName, "image/png", content, CancellationToken.None);
    }
}