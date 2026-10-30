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
    IReadOnlyList<string> ShadowedNames,
    IReadOnlyList<PromptSkill> Skills);

internal sealed class ThreadSession : IAsyncDisposable
{
    private readonly ThreadSessionData _data;
    private int _isDisposed;

    public IReadOnlyList<AITool> Tools => _data.Tools;
    public McpClientManager ClientManager => _data.ClientManager;
    public IReadOnlyList<PromptSection> FileSystemPrompts => _data.FileSystemPrompts;

    // The skills this session's servers ship, granted by granting the servers: a worker allowed a
    // server is taught its skills, and one that is not is not.
    public IReadOnlyList<PromptSkill> Skills => _data.Skills;
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
        ReadImageSupport? readImages = null,
        IVfsBridge? bridge = null,
        bool noTools = false,
        IReadOnlySet<string>? withheldTools = null,
        CallTokens? callTokens = null)
    {
        var builder = new ThreadSessionBuilder(endpoints, name, description,
            userId, domainTools, filesystemEnabledTools, loggerFactory, promptCache, readImages, bridge, noTools,
            withheldTools, callTokens);
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
    ReadImageSupport? readImages = null,
    IVfsBridge? bridge = null,
    bool noTools = false,
    IReadOnlySet<string>? withheldTools = null,
    CallTokens? callTokens = null)
{
    private static readonly HashSet<string> _fileSystemMcpToolNames = [.. FileSystemOperations.ToolNames];

    // Channel-protocol tools are invoked directly by the channel connection layer, never by the LLM.
    // A dual-role server (e.g. mcp-scheduling, which is both a channel and a filesystem tool server)
    // exposes them on the same /mcp endpoint, so they leak into the agent-visible tool list unless stripped.
    private static readonly HashSet<string> _channelProtocolToolNames =
    [
        ChannelProtocol.SendReplyTool,
        ChannelProtocol.RequestApprovalTool,
        ChannelProtocol.OfferApprovalTool,
        ChannelProtocol.SettleApprovalTool,
        ChannelProtocol.CreateConversationTool,
        ChannelProtocol.RegisterAgentsTool,
        ChannelProtocol.ReceiveTool,
        ChannelProtocol.FetchAttachmentTool,
        ChannelProtocol.StoreAttachmentTool,
        // Not the channel's, but the operator commands' on the same /mcp, and as far from a model.
        ChannelProtocol.ListDelegationsTool,
        ChannelProtocol.ReplaceDelegationTool
    ];

    public async Task<ThreadSessionData> BuildAsync(CancellationToken ct)
    {
        var dialLogger = loggerFactory?.CreateLogger(typeof(McpClientManager).FullName!);
        // The same store file_read writes to. An MCP server returning an image gets eviction and
        // hydration through the bridge without knowing either exists.
        var clientManager = await McpClientManager.CreateAsync(
            name, userId, description, endpoints, new McpClientHandlers(), promptCache, dialLogger,
            readImages?.Store, callTokens, ct);

        // Dialled for its prompts alone: the servers still say how this agent works, but nothing
        // they offer to call or to load reaches it, and no mount is made that no tool could read.
        // No skills is also what keeps the preload judge from being asked on its turns.
        if (noTools)
        {
            return new ThreadSessionData(clientManager, [], null, [], [], [], []);
        }

        IVirtualFileSystemRegistry? registry = null;
        IReadOnlyList<AIFunction> fileSystemTools = [];
        IReadOnlyList<PromptSection> fileSystemPrompts = [];
        var fsLogger = loggerFactory?.CreateLogger(typeof(McpFileSystemDiscovery).FullName!)
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var fsRegistry = new VirtualFileSystemRegistry();
        var shadowed = await McpFileSystemDiscovery.DiscoverAndMountAsync(
            clientManager.Clients, clientManager.DynamicClients, fsRegistry, fsLogger, ct);

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
                var feature = new FileSystemToolFeature(registry, readImages, bridge);
                fileSystemTools = feature.GetTools(fsFeatureConfig).ToList();
                fileSystemPrompts = feature.Prompt is { } mounts
                    ? [PromptManifest.Bind(PromptManifest.FilesystemMounts, mounts)]
                    : [];
            }
        }

        // Channel-protocol tools are always stripped; raw fs_* tools are stripped when their
        // domain filesystem wrappers are active, to avoid exposing duplicate functionality to the LLM.
        var mcpTools = FilterMcpTools(clientManager.Tools, fileSystemTools.Count > 0, withheldTools);
        var tools = Offered(mcpTools, domainTools, fileSystemTools);

        return new ThreadSessionData(
            clientManager, tools, registry, fileSystemPrompts,
            [.. fsRegistry.GetMounts().Select(m => m.Name)], shadowed, clientManager.Skills);
    }

    // The filesystem tools lead; the rest keep the order they had. The list is read in order, and
    // a model batching a read beside another call takes an early tool that sounds right: with the
    // web tools first, Haiku 5.5 browsed or searched for /ha/setup-index.md in a quarter of the
    // turns that should have read it; with these first, never (2026-10-10). A server's own tools
    // still come before the subagent's, so a throwaway first call lands where it always did.
    internal static IReadOnlyList<AITool> Offered(
        IReadOnlyList<AITool> mcpTools,
        IReadOnlyList<AITool> domainTools,
        IReadOnlyList<AITool> fileSystemTools) =>
        [.. fileSystemTools, .. mcpTools, .. domainTools];

    internal static IReadOnlyList<AITool> FilterMcpTools(
        IReadOnlyList<AITool> mcpTools, bool filesystemToolsActive, IReadOnlySet<string>? withheld = null)
    {
        HashSet<string> withheldNames = [.. withheld ?? new HashSet<string>()];
        return mcpTools
            .Where(t => !HasReservedSuffix(t.Name, _channelProtocolToolNames))
            .Where(t => !HasReservedSuffix(t.Name, withheldNames))
            .Where(t => !filesystemToolsActive || !HasReservedSuffix(t.Name, _fileSystemMcpToolNames))
            .ToList();
    }

    private static bool HasReservedSuffix(string toolName, HashSet<string> reserved)
    {
        return reserved.Any(n => toolName.EndsWith($"__{n}", StringComparison.Ordinal));
    }
}