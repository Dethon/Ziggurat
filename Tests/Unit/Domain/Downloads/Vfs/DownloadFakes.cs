using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;

namespace Tests.Unit.Domain.Downloads.Vfs;

// Shared test doubles for the downloads overlay, the library fs-tool routing, and the
// completion watcher tests. Keep the public surface stable: FakeDownloadClient.Items/CleanedUp,
// FakeRoutingStore.Entries, and RecordingFileSystemClient.RemovedDirectories/GlobResults
// are read by all three test areas.
public static class DownloadFakes
{
    public static DownloadItem Item(int id, DownloadState state = DownloadState.InProgress) => new()
    {
        Id = id,
        Title = $"Download {id}",
        Link = $"magnet:{id}",
        State = state,
        Progress = state == DownloadState.Completed ? 1.0 : 0.5,
        DownSpeed = 1.5,
        UpSpeed = 0.25,
        Eta = 12,
        SavePath = $"/downloads/{id}",
        Size = 1024
    };

    public static DownloadsOverlay BuildOverlay(
        string libraryRoot,
        out FakeDownloadClient client,
        out FakeRoutingStore routing,
        out RecordingFileSystemClient disk)
    {
        client = new FakeDownloadClient();
        routing = new FakeRoutingStore();
        disk = new RecordingFileSystemClient();
        return new DownloadsOverlay(client, routing, disk, new LibraryPathConfig(libraryRoot));
    }

    public sealed class FakeDownloadClient : IDownloadClient
    {
        public List<DownloadItem> Items { get; } = new();
        public List<int> CleanedUp { get; } = new();

        public void Add(DownloadItem item)
        {
            Items.RemoveAll(i => i.Id == item.Id);
            Items.Add(item);
        }

        // Set to make the manager-side cancel fail, which must abort the delete before any
        // housekeeping runs — a routing entry cleared for a download that is still going would
        // orphan it.
        public Exception? CleanupFailure { get; set; }

        public Task Cleanup(int id, CancellationToken cancellationToken = default)
        {
            if (CleanupFailure is not null)
            {
                throw CleanupFailure;
            }

            CleanedUp.Add(id);
            Items.RemoveAll(i => i.Id == id);
            return Task.CompletedTask;
        }

        public Task<DownloadItem?> GetDownloadItem(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(i => i.Id == id));

        public Task<IReadOnlyList<DownloadItem>> GetDownloadItems(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DownloadItem>>(Items.ToList());

        public Task Download(string link, string savePath, int id, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class FakeRoutingStore : IDownloadRoutingStore
    {
        public List<DownloadRouting> Entries { get; } = new();

        public Task SetAsync(DownloadRouting routing, CancellationToken ct = default)
        {
            Entries.RemoveAll(r => r.DownloadId == routing.DownloadId);
            Entries.Add(routing);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DownloadRouting>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DownloadRouting>>(Entries.ToList());

        public Task RemoveAsync(int downloadId, CancellationToken ct = default)
        {
            Entries.RemoveAll(r => r.DownloadId == downloadId);
            return Task.CompletedTask;
        }
    }

    public sealed class RecordingFileSystemClient : IFileSystemClient
    {
        public List<string> RemovedDirectories { get; } = new();
        public List<string> GlobResults { get; } = new();

        public Task RemoveDirectory(string path, CancellationToken cancellationToken = default)
        {
            RemovedDirectories.Add(path);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, string[]>> DescribeDirectory(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Dictionary<string, string[]>());

        // Mirrors the real client, which throws when the glob's base directory is missing —
        // opt-in so the suites that never touch the disk keep their empty-result shortcut.
        public bool ThrowIfBaseMissing { get; set; }

        public GlobWalk Glob(string basePath, string pattern, CancellationToken cancellationToken = default) =>
            new(_ => Yield(basePath));

        // Lazily, like the real walk: a missing base directory is raised on the first pull, which
        // is where the tool's not-found envelope now catches it.
        private async IAsyncEnumerable<string> Yield(string basePath)
        {
            await Task.Yield();
            if (ThrowIfBaseMissing && !Directory.Exists(basePath))
            {
                throw new DirectoryNotFoundException(basePath);
            }

            foreach (var hit in GlobResults)
            {
                yield return hit;
            }
        }

        public Task Move(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFile(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public List<string> TrashedPaths { get; } = new();

        public Task<string> MoveToTrash(string path, CancellationToken cancellationToken = default)
        {
            TrashedPaths.Add(path);
            return Task.FromResult(path);
        }
    }
}