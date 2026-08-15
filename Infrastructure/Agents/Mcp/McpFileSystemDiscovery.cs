using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mcp;

internal static class McpFileSystemDiscovery
{
    private const string ResourcePrefix = "filesystem://";

    // Answers the mount names it refused, because a shadowed mount is the one way a filesystem can
    // be perfectly valid and simply not there — and the machine serving it has no way to find that
    // out for itself.
    public static async Task<IReadOnlyList<string>> DiscoverAndMountAsync(
        IReadOnlyList<McpClient> clients,
        VirtualFileSystemRegistry registry,
        ILogger logger,
        CancellationToken ct)
    {
        var perClient = await Task.WhenAll(clients
            .Where(c => c.ServerCapabilities.Resources is not null)
            .Select(client => GatherMountsAsync(client, logger, ct)));

        // In the order the endpoints were dialled, which puts the deployment's own filesystems
        // before any outpost. A mount point already taken is a collision the newcomer loses: it is
        // shadowed, the existing mount is untouched, and the fact is logged, because at the machine
        // this looks exactly like a registration that worked and a mount that never appeared.
        var shadowed = new List<string>();
        foreach (var (mount, backend) in perClient.SelectMany(m => m))
        {
            if (registry.TryMount(mount, backend))
            {
                logger.LogInformation("Discovered filesystem '{Name}' at mount point '{MountPoint}'",
                    mount.Name, mount.MountPoint);
                continue;
            }

            shadowed.Add(mount.Name);
            logger.LogWarning(
                "Filesystem '{Name}' is shadowed: mount point '{MountPoint}' is already another "
                + "mount's, so it was not mounted and the existing one is untouched",
                mount.Name, mount.MountPoint);
        }

        return shadowed;
    }

    private static async Task<IReadOnlyList<(FileSystemMount Mount, McpFileSystemBackend Backend)>> GatherMountsAsync(
        McpClient client, ILogger logger, CancellationToken ct)
    {
        var resources = await client.ListResourcesAsync(cancellationToken: ct);
        var filesystemResources = resources
            .Where(r => r.Uri.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filesystemResources.Count == 0)
        {
            return [];
        }

        // Tool registration is per-server, so the same capability set applies to every filesystem
        // this client exposes; list once. The backends get the whole set, not just the model-facing
        // part of it: the move-out check is advertised like any other operation, and the proxy asks
        // it only of a server that registered it.
        var advertisedTools = await client.ListToolsAsync(cancellationToken: ct);
        var advertised = AdvertisedOperations(advertisedTools.Select(t => t.Name));
        var capabilities = DeriveCapabilities(advertised);

        var mounts = await Task.WhenAll(filesystemResources.Select(async resource =>
        {
            try
            {
                var content = await client.ReadResourceAsync(resource.Uri, cancellationToken: ct);
                var text = string.Join("", content.Contents
                    .OfType<TextResourceContents>()
                    .Select(c => c.Text));

                var metadata = JsonSerializer.Deserialize<FileSystemResourceMetadata>(text,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (metadata is null || string.IsNullOrEmpty(metadata.Name) || string.IsNullOrEmpty(metadata.MountPoint))
                {
                    logger.LogWarning("Invalid filesystem resource metadata at {Uri}", resource.Uri);
                    return ((FileSystemMount Mount, McpFileSystemBackend Backend)?)null;
                }

                var mount = new FileSystemMount(metadata.Name, metadata.MountPoint, metadata.Description ?? "")
                {
                    Capabilities = capabilities,
                    Workspace = metadata.Workspace,
                    IsLandingTarget = metadata.LandingTarget
                };
                var backend = new McpFileSystemBackend(client, metadata.Name, advertised, logger);
                return (mount, backend);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read filesystem resource at {Uri}", resource.Uri);
                return null;
            }
        }));

        return mounts.Where(m => m is not null).Select(m => m!.Value).ToList();
    }

    // A server advertises exactly the fs_* tools its backend implements (an operation it never
    // overrode is never registered), so its advertised tool set is the single source of truth for
    // what its mounts can do. A server may publish a tool under a prefixed name, so the operation
    // is read off the suffix; a name that is no operation of ours is dropped.
    internal static IReadOnlySet<string> AdvertisedOperations(IEnumerable<string> advertisedToolNames)
    {
        var names = advertisedToolNames.ToList();
        return FileSystemOperations.All
            .Where(o => names.Any(name =>
                name.Equals(o.ToolName, StringComparison.Ordinal) ||
                name.EndsWith($"__{o.ToolName}", StringComparison.Ordinal)))
            .Select(o => o.ToolName)
            .ToHashSet(StringComparer.Ordinal);
    }

    // What the mount publishes to the model: the advertised operations it can call, named by the
    // domain-tool leaf name the LLM actually uses, in the operation list's canonical display order.
    // The transfer machinery's operations have no capability and so appear in no list.
    internal static IReadOnlyList<string> DeriveCapabilities(IReadOnlySet<string> advertisedOperations) =>
        FileSystemOperations.All
            .Where(o => o.Capability is not null && advertisedOperations.Contains(o.ToolName))
            .Select(o => o.Capability!)
            .ToList();

    // LandingTarget is not nullable: a server that predates the claim publishes no field, which
    // binds to false, and false is what a mount that never said so must mean.
    private record FileSystemResourceMetadata(
        string Name, string MountPoint, string? Description, string? Workspace, bool LandingTarget);
}