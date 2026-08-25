using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Prompts;
using Domain.Tools.FileSystem;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents;

internal sealed record ThreadSessionData(
    McpClientManager ClientManager,
    IReadOnlyList<AITool> Tools,
    IVirtualFileSystemRegistry? FileSystemRegistry,
    IReadOnlyList<PromptSection> FileSystemPrompts,
    IReadOnlyList<string> MountedNames,
    IReadOnlyList<string> ShadowedNames);

internal sealed class ThreadSession : IAsyncDisposable
{
    private readonly ThreadSessionData _data;
    private int _isDisposed;

    public IReadOnlyList<AITool> Tools => _data.Tools;
    public McpClientManager ClientManager => _data.ClientManager;
    public IReadOnlyList<PromptSection> FileSystemPrompts => _data.FileSystemPrompts;
    public IVirtualFileSystemRegistry? FileSystemRegistry => _data.FileSystemRegistry;

    // What this build made of the filesystems it found. Read once, by the step that writes each
    // outpost's verdict back onto its registration — the collision is only knowable here.
    public IReadOnlyList<string> MountedNames => _data.MountedNames;
    public IReadOnlyList<string> ShadowedNames => _data.ShadowedNames;

    private ThreadSession(ThreadSessionData data)
    {
        _data = data;
    }

    public static async Task<ThreadSession> CreateAsync(
        IReadOnlyList<McpServerEndpoint> endpoints,
        string name,
        string userId,
        string description,
        IReadOnlyList<AIFunction> domainTools,
        IReadOnlySet<string> filesystemEnabledTools,
        ILoggerFactory? loggerFactory,
        CancellationToken ct,
        McpPromptCache? promptCache = null,
        ReadImageSupport? readImages = null)
    {
        var builder = new ThreadSessionBuilder(endpoints, name, description,
            userId, domainTools, filesystemEnabledTools, loggerFactory, promptCache, readImages);
        var data = await builder.BuildAsync(ct);
        return new ThreadSession(data);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        await _data.ClientManager.DisposeAsync();
    }
}

internal sealed class ThreadSessionBuilder(
    IReadOnlyList<McpServerEndpoint> endpoints,
    string name,
    string description,
    string userId,
    IReadOnlyList<AIFunction> domainTools,
    IReadOnlySet<string> filesystemEnabledTools,
    ILoggerFactory? loggerFactory,
    McpPromptCache? promptCache = null,
    ReadImageSupport? readImages = null)
{
    private static readonly HashSet<string> _fileSystemMcpToolNames = [.. FileSystemOperations.ToolNames];

    // Channel-protocol tools are invoked directly by the channel connection layer, never by the LLM.
    // A dual-role server (e.g. mcp-scheduling, which is both a channel and a filesystem tool server)
    // exposes them on the same /mcp endpoint, so they leak into the agent-visible tool list unless stripped.
    private static readonly HashSet<string> _channelProtocolToolNames =
    [
        ChannelProtocol.SendReplyTool,
        ChannelProtocol.RequestApprovalTool,
        ChannelProtocol.CreateConversationTool,
        ChannelProtocol.RegisterAgentsTool,
        ChannelProtocol.ReceiveTool,
        ChannelProtocol.FetchAttachmentTool
    ];

    public async Task<ThreadSessionData> BuildAsync(CancellationToken ct)
    {
        var dialLogger = loggerFactory?.CreateLogger(typeof(McpClientManager).FullName!);
        // The same store file_read writes to. An MCP server returning an image gets eviction and
        // hydration through the bridge without knowing either exists.
        var clientManager = await McpClientManager.CreateAsync(
            name, userId, description, endpoints, new McpClientHandlers(), promptCache, dialLogger,
            readImages?.Store, ct);

        IVirtualFileSystemRegistry? registry = null;
        IReadOnlyList<AIFunction> fileSystemTools = [];
        IReadOnlyList<PromptSection> fileSystemPrompts = [];
        var fsLogger = loggerFactory?.CreateLogger(typeof(McpFileSystemDiscovery).FullName!)
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var fsRegistry = new VirtualFileSystemRegistry();
        var shadowed = await McpFileSystemDiscovery.DiscoverAndMountAsync(
            clientManager.Clients, fsRegistry, fsLogger, ct);

        if (fsRegistry.GetMounts().Count > 0)
        {
            if (filesystemEnabledTools.Count == 0)
            {
                var mountNames = string.Join(", ", fsRegistry.GetMounts().Select(m => m.Name));
                fsLogger.LogDebug(
                    "MCP servers expose filesystem resources ({Mounts}) but the 'filesystem' feature is not enabled for this agent. " +
                    "Add 'filesystem' to enabledFeatures to use virtual filesystem tools",
                    mountNames);
            }
            else
            {
                registry = fsRegistry;
                var fsFeatureConfig = new FeatureConfig(EnabledTools: filesystemEnabledTools);
                var feature = new FileSystemToolFeature(registry, readImages);
                fileSystemTools = feature.GetTools(fsFeatureConfig).ToList();
                fileSystemPrompts = feature.Prompt is { } mounts
                    ? [PromptManifest.Bind(PromptManifest.FilesystemMounts, mounts)]
                    : [];
            }
        }

        // Channel-protocol tools are always stripped; raw fs_* tools are stripped when their
        // domain filesystem wrappers are active, to avoid exposing duplicate functionality to the LLM.
        var mcpTools = FilterMcpTools(clientManager.Tools, fileSystemTools.Count > 0);
        var tools = mcpTools.Concat(domainTools).Concat(fileSystemTools).ToList();

        return new ThreadSessionData(
            clientManager, tools, registry, fileSystemPrompts,
            [.. fsRegistry.GetMounts().Select(m => m.Name)], shadowed);
    }

    internal static IReadOnlyList<AITool> FilterMcpTools(IReadOnlyList<AITool> mcpTools, bool filesystemToolsActive)
    {
        return mcpTools
            .Where(t => !HasReservedSuffix(t.Name, _channelProtocolToolNames))
            .Where(t => !filesystemToolsActive || !HasReservedSuffix(t.Name, _fileSystemMcpToolNames))
            .ToList();
    }

    private static bool HasReservedSuffix(string toolName, HashSet<string> reserved)
    {
        return reserved.Any(n => toolName.EndsWith($"__{n}", StringComparison.Ordinal));
    }
}