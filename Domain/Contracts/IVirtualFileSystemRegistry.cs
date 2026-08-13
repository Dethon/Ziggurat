using Domain.DTOs;
using Domain.DTOs.FileSystem;

namespace Domain.Contracts;

public record FileSystemResolution(IFileSystemBackend Backend, string RelativePath, string MountPoint = "")
{
    // The one implementation of the mount-point translation. A tool answers in the coordinates it
    // was asked in, so a path the backend produced and the caller never named — a glob entry, a
    // search hit, an entry of a directory transfer — gets its mount point prefixed here. Backends
    // disagree about the leading slash, so it is trimmed; a trailing one marks a directory and is
    // left alone. Where the caller did name the path, echo their own string instead: at least one
    // backend answers with the container-absolute path, and prefixing a mount point onto that
    // produces nonsense.
    public string ToVirtualPath(string backendPath) => ToVirtualPath(MountPoint, backendPath);

    // The same translation for a caller holding a mount rather than a resolution: landing composes
    // the directory an attachment goes in from the mount point and the workspace the mount declares,
    // before there is any path to resolve. Static so that stays one implementation rather than two.
    public static string ToVirtualPath(string mountPoint, string backendPath) =>
        $"{mountPoint.TrimEnd('/')}/{backendPath.TrimStart('/')}";
}

public interface IVirtualFileSystemRegistry
{
    void Mount(FileSystemMount mount, IFileSystemBackend backend);

    // Resolution is data, not an exception: a path with no mount prefix is the mistake the
    // filesystem prompt warns the model about, and it must come back as the envelope the prompt
    // promises rather than unwinding twelve tool call sites that none of them guard.
    FsResult<FileSystemResolution> Resolve(string virtualPath);

    IReadOnlyList<FileSystemMount> GetMounts();
}