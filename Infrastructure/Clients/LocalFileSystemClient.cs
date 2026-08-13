using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.Tools.FileSystem;

namespace Infrastructure.Clients;

public class LocalFileSystemClient : IFileSystemClient
{
    public const string TrashFolderName = ".trash";

    // The jail vets the argument path, but a symlink discovered inside a tree can point
    // anywhere — recursive walks and copies must not follow one (or cycle on it), so they
    // skip symlinks wholesale. AttributesToSkip is set explicitly because the default also
    // skips hidden and system entries, which these operations have always included. The
    // recursive walks take the same guard from DiskWalk, which is where they now share one pass.
    private static readonly EnumerationOptions _skipSymlinks = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public Task<Dictionary<string, string[]>> DescribeDirectory(string path,
        CancellationToken cancellationToken = default)
    {
        return !Directory.Exists(path)
            ? throw new DirectoryNotFoundException($"Library directory not found: {path}")
            : Task.FromResult(GetLibraryPaths(path));
    }

    // One lazy pass over the tree, matching each entry as it is found. The caller stops pulling
    // once it has all it will report, so a broad pattern over a large tree costs what the response
    // costs rather than what the tree does. Matching is the shared glob matcher every other mount
    // uses, so one pattern language covers them all; the entry's own directory flag answers the
    // dirs-only rule that used to need a second walk.
    public GlobWalk Glob(string basePath, string pattern, CancellationToken cancellationToken = default)
    {
        var dirsOnly = pattern.EndsWith('/');
        // Virtual paths are always '/'-separated, but a pattern relativized against the glob root
        // arrives in the platform's spelling; the matcher would read a '\' as a literal.
        var effectivePattern = (dirsOnly ? pattern.TrimEnd('/') : pattern).Replace('\\', '/');
        var matches = GlobRegex.CompileMatcher(effectivePattern);

        return new GlobWalk(Walk(basePath, matches, dirsOnly, cancellationToken));
    }

    private static async IAsyncEnumerable<string> Walk(
        string basePath,
        Func<string, bool> matches,
        bool dirsOnly,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The walk is synchronous I/O, so it leaves the caller's stack before touching the disk;
        // a caller that never pulls pays nothing.
        await Task.Yield();

        foreach (var entry in DiskWalk.Entries(basePath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (dirsOnly && !entry.IsDirectory)
            {
                continue;
            }

            if (!matches(Path.GetRelativePath(basePath, entry.Path).Replace('\\', '/')))
            {
                continue;
            }

            yield return entry.IsDirectory ? entry.Path + "/" : entry.Path;
        }
    }

    public Task Move(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            throw new IOException($"Source path {sourcePath} does not exist");
        }

        CreateDestinationParentPath(destinationPath);
        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, destinationPath);
        }
        else if (Directory.Exists(sourcePath))
        {
            MoveDirectory(sourcePath, destinationPath);
        }

        return Task.CompletedTask;
    }

    public Task RemoveDirectory(string path, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }

        return Task.CompletedTask;
    }

    public Task RemoveFile(string path, CancellationToken cancellationToken = default)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<string> MoveToTrash(string path, CancellationToken cancellationToken = default)
    {
        var isFile = File.Exists(path);
        var isDirectory = Directory.Exists(path);

        if (!isFile && !isDirectory)
        {
            throw new IOException($"Path not found: {path}");
        }

        var name = Path.GetFileName(path);
        var trashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), TrashFolderName);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var trashPath = Path.Combine(trashDir, $"{timestamp}_{uniqueId}_{name}");

        CreateDestinationParentPath(trashPath);

        if (isFile)
        {
            File.Move(path, trashPath);
        }
        else
        {
            MoveDirectory(path, trashPath);
        }

        return Task.FromResult(trashPath);
    }

    private static Dictionary<string, string[]> GetLibraryPaths(string basePath)
    {
        // ReSharper disable once ConvertClosureToMethodGroup | It messes with the nullability checks somehow
        return Directory
            .EnumerateFiles(basePath, "*", SearchOption.AllDirectories)
            .GroupBy(
                x => Path.GetDirectoryName(x) ?? string.Empty,
                x => Path.GetFileName(x))
            .Where(x => !string.IsNullOrEmpty(x.Key))
            .ToDictionary(
                x => x.Key,
                x => x.Where(y => !string.IsNullOrEmpty(y)).ToArray());
    }

    private static void MoveDirectory(string sourcePath, string destinationPath)
    {
        try
        {
            Directory.Move(sourcePath, destinationPath);
        }
        catch (IOException)
        {
            // Directory.Move fails with EXDEV across filesystem boundaries;
            // fall back to recursive copy + delete
            CopyDirectory(sourcePath, destinationPath);
            Directory.Delete(sourcePath, true);
        }
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", _skipSymlinks))
        {
            File.Copy(file, Path.Combine(destinationPath, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.EnumerateDirectories(sourcePath, "*", _skipSymlinks))
        {
            CopyDirectory(dir, Path.Combine(destinationPath, Path.GetFileName(dir)));
        }
    }

    private static void CreateDestinationParentPath(string destinationPath)
    {
        var parentPath = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(parentPath) || Directory.Exists(parentPath) || File.Exists(parentPath))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(parentPath);
            return;
        }

        const UnixFileMode mode = UnixFileMode.UserRead |
                                  UnixFileMode.UserWrite |
                                  UnixFileMode.UserExecute |
                                  UnixFileMode.GroupRead |
                                  UnixFileMode.GroupWrite |
                                  UnixFileMode.GroupExecute |
                                  UnixFileMode.OtherRead |
                                  UnixFileMode.OtherExecute;
        Directory.CreateDirectory(parentPath, mode);
    }
}