using System.Text.Json;
using Domain.Contracts;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Infrastructure.Utils;

// The twin of AddFileSystemTools. The tools come from what the backend overrides; the mount's
// identity comes from its one name. The address the resource is published at, the name the agent
// addresses the mount by and the path it is mounted under are all derived here, so a mismatch stops
// being representable — the same guarantee the tool registrar gives for capabilities.
//
// Never hand-write a filesystem resource, the same way you never hand-write an fs_* tool.
public static class FileSystemServerResource
{
    private const string Scheme = "filesystem://";

    public static IMcpServerBuilder AddFileSystemResource<TBackend>(this IMcpServerBuilder builder)
        where TBackend : FileSystemBackendBase
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<McpServerResource>(sp =>
        {
            var backend = sp.GetRequiredService<TBackend>();
            return McpServerResource.Create(
                () => Describe(backend),
                new McpServerResourceCreateOptions
                {
                    UriTemplate = Address(backend.FilesystemName),
                    Name = backend.FilesystemName,
                    Description = backend.DescribeMount,
                    MimeType = "application/json"
                });
        });

        return builder;
    }

    public static string Address(string filesystemName) => Scheme + filesystemName;

    // What McpFileSystemDiscovery reads to mount this filesystem: the name it will be addressed by,
    // the path it goes under, the prose the model gets about it, and where under it a file can be
    // written and will stay written. All four come off the backend, the last one null for a mount
    // that declares no workspace.
    public static string Describe(FileSystemBackendBase backend) =>
        JsonSerializer.Serialize(new
        {
            name = backend.FilesystemName,
            mountPoint = backend.MountPoint,
            description = backend.DescribeMount,
            workspace = backend.Workspace
        });
}