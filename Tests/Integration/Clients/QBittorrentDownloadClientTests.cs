using Domain.DTOs;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

public class QBittorrentDownloadClientTests(QBittorrentFixture fixture) : IClassFixture<QBittorrentFixture>
{
    [Fact]
    public async Task Cleanup_WhenTorrentDoesNotExist_DoesNotThrow()
    {
        // Arrange
        var client = fixture.CreateClient();
        const int nonExistentId = 999998;

        // Act & Assert - should not throw
        await Should.NotThrowAsync(() => client.Cleanup(nonExistentId, CancellationToken.None));
    }

    [Fact]
    public async Task Download_AndCleanup_RemovesTorrent()
    {
        // Arrange
        var client = fixture.CreateClient();
        // Ubuntu 24.04 - a well-seeded public domain torrent
        const string magnetLink =
            "magnet:?xt=urn:btih:KRWPCX3SJUM4IMM4YF3MVSJIBFTHVFCS&dn=ubuntu-24.04-desktop-amd64.iso";
        const string savePath = "/downloads";
        var id = new Random().Next(100000, 999999);

        // Act - Add torrent
        await client.Download(magnetLink, savePath, id, CancellationToken.None);

        var downloadItem = await client.GetDownloadItem(id, CancellationToken.None);
        downloadItem.ShouldNotBeNull();

        // Act - Cleanup
        await client.Cleanup(id, CancellationToken.None);

        // Assert - Should be removed
        var afterCleanup = await client.GetDownloadItem(id, CancellationToken.None);
        afterCleanup.ShouldBeNull();
    }

    [Fact]
    public async Task GetDownloadItem_WhenTorrentIsStopped_ReportsPausedState()
    {
        // Arrange - qBittorrent 5.x reports stopped torrents as "stoppedDL" (renamed from "pausedDL")
        var client = fixture.CreateClient();
        const string magnetLink =
            "magnet:?xt=urn:btih:KRWPCX3SJUM4IMM4YF3MVSJIBFTHVFCS&dn=ubuntu-24.04-desktop-amd64.iso";
        const string savePath = "/downloads";
        var id = new Random().Next(100000, 999999);

        try
        {
            await client.Download(magnetLink, savePath, id, CancellationToken.None);

            // Act
            await fixture.StopAllTorrentsAsync();
            var downloadItem = await client.GetDownloadItem(id, CancellationToken.None);

            // Assert
            downloadItem.ShouldNotBeNull();
            downloadItem.State.ShouldBe(DownloadState.Paused);
        }
        finally
        {
            await client.Cleanup(id, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetDownloadItems_ReturnsOwnedTorrents()
    {
        // Arrange
        var client = fixture.CreateClient();
        const string magnetLink =
            "magnet:?xt=urn:btih:KRWPCX3SJUM4IMM4YF3MVSJIBFTHVFCS&dn=ubuntu-24.04-desktop-amd64.iso";
        const string savePath = "/downloads";
        var id = new Random().Next(100000, 999999);

        try
        {
            // Act - Add torrent
            await client.Download(magnetLink, savePath, id, CancellationToken.None);

            // Assert - It appears in the bulk listing with the correct Id
            var items = await client.GetDownloadItems(CancellationToken.None);
            var match = items.FirstOrDefault(x => x.Id == id);
            match.ShouldNotBeNull();
            match.Id.ShouldBe(id);
        }
        finally
        {
            await client.Cleanup(id, CancellationToken.None);
        }
    }
}