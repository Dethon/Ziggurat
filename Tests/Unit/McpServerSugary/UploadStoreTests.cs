using Domain.DTOs;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpServerSugary.Channel;
using McpServerSugary.Modules;
using McpServerSugary.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// A meal's photos outlive every turn that may hydrate them (ADR-0020), and then go: hydration asks
// for a photo as long as its reference is within twenty messages of a turn, and the reference lives
// as long as the conversation does, so the store keeps a photo for the deployment's attachment
// retention and sweeps it after. A swept photo reads back as nothing, which the agent turns into a
// placeholder naming the file.
public sealed class UploadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sugary-uploads-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly RetentionSettings _retention = new();
    private readonly UploadStore _store;

    public UploadStoreTests() =>
        _store = new UploadStore(
            new UploadSettings { StoragePath = _root }, _retention, _clock, NullLogger<UploadStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // The server reads its window from the deployment's one retention file, and that window is at
    // least the life of the conversation whose history holds the reference: no turn can reach a
    // photo the store has already let go of while the conversation still exists.
    [Fact]
    public void TheWindow_IsTheDeploymentsAttachmentRetention_AndOutlivesTheConversation()
    {
        var settings = new ConfigurationBuilder().BindSettings<SugarySettings>();

        settings.Retention.AttachmentRetention.ShouldBe(TimeSpan.FromDays(365));
        settings.Retention.AttachmentRetention.ShouldBeGreaterThanOrEqualTo(settings.Retention.PurgeHorizon);
    }

    [Fact]
    public async Task TheSweep_TakesPhotosOlderThanTheWindow_AndKeepsYoungerOnes()
    {
        var old = await SaveAsync("meal-1", [1, 2, 3]);
        _clock.Advance(_retention.AttachmentRetention + TimeSpan.FromDays(1));
        var fresh = await SaveAsync("meal-2", [4, 5, 6]);

        _store.Sweep().ShouldBe(1);

        (await _store.ReadBytesAsync(old.Id, CancellationToken.None)).ShouldBeNull();
        (await _store.ReadBytesAsync(fresh.Id, CancellationToken.None)).ShouldBe([4, 5, 6]);
        Directory.GetDirectories(_root).Length.ShouldBe(1);
    }

    // A photo a day short of the window is still one a quiet conversation's next turn may need.
    [Fact]
    public async Task TheSweep_KeepsAPhotoUntilItsWindowHasPassed()
    {
        var photo = await SaveAsync("meal-1", [1]);
        _clock.Advance(_retention.AttachmentRetention - TimeSpan.FromDays(1));

        _store.Sweep().ShouldBe(0);

        (await _store.ReadBytesAsync(photo.Id, CancellationToken.None)).ShouldBe([1]);
    }

    // A write the server died in the middle of holds nothing a turn can name, so it does not wait
    // out the window: an hour after it was begun, it goes.
    [Fact]
    public void TheSweep_TakesAnAttachmentThatWasNeverCompleted()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "meal-1", "half-written"));
        directory.LastWriteTimeUtc = (_clock.GetUtcNow() - TimeSpan.FromHours(2)).UtcDateTime;

        _store.Sweep().ShouldBe(1);

        Directory.GetDirectories(_root).ShouldBeEmpty();
    }

    // An attachment with no metadata yet may be one being written right now, its bytes landing
    // before the metadata that completes it: the sweep leaves it to finish.
    [Fact]
    public void TheSweep_LeavesAnAttachmentStillBeingWritten()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "meal-1", "being-written"));
        File.WriteAllBytes(Path.Combine(directory.FullName, "meal-0.jpg"), [1, 2, 3]);
        directory.LastWriteTimeUtc = _clock.GetUtcNow().UtcDateTime;

        _store.Sweep().ShouldBe(0);

        Directory.Exists(directory.FullName).ShouldBeTrue();
    }

    [Fact]
    public void TheServer_RunsTheSweep()
    {
        var services = new ServiceCollection().ConfigureSugary(new SugarySettings());

        services.ShouldContain(d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(UploadSweeper));
    }

    private Task<AttachmentReference> SaveAsync(string conversation, byte[] bytes) =>
        _store.SaveAsync(conversation, "meal-0.jpg", "image/jpeg", bytes, CancellationToken.None);
}