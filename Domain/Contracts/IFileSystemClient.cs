namespace Domain.Contracts;

public interface IFileSystemClient
{
    Task<Dictionary<string, string[]>> DescribeDirectory(string path, CancellationToken cancellationToken = default);
    GlobWalk Glob(string basePath, string pattern, CancellationToken cancellationToken = default);
    Task Move(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
    Task RemoveDirectory(string path, CancellationToken cancellationToken = default);
    Task RemoveFile(string path, CancellationToken cancellationToken = default);
    Task<string> MoveToTrash(string path, CancellationToken cancellationToken = default);
}