using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Security;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Mcp.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Integration.Agents;

// The model reaches a mount through the domain filesystem tools, which dispatch to the server's
// fs_* tools through McpFileSystemBackend — a second hop that used to carry no `_meta`. The first
// prod watch failed on exactly that: the mount asked who was calling and nobody had said. The
// backend stamps the conversation the tool call's own arguments name, the way a directly-called
// tool carries its turn's.
public class McpFileSystemBackendMetaTests
{
    private sealed record ProbeSettings(string Name) : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    [Fact]
    public async Task AFileToolCall_CarriesTheConversationItsArgumentsNameToTheServer()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new ProbeSettings("probe"))
            .WithTools<MetaEchoingFileSystemTools>());
        var registry = new VirtualFileSystemRegistry();
        registry.Mount(new FileSystemMount("probe", "/probe", "probe"),
            new McpFileSystemBackend(server.Client, "probe", advertisedOperations: null));
        var context = new ConversationContext("jonas", "conv-1", "fran", new ReplyTarget("telegram", "conv-1"));

        var info = await new VfsFileInfoTool(registry).RunAsync("/probe/anything", new AIFunctionArguments
        {
            Context = new Dictionary<object, object?> { [ConversationContext.ContextKey] = context }
        });

        info["lastModified"]!.GetValue<string>().ShouldBe("jonas:conv-1");
    }

    // The mount as discovered is nobody's: the invocation in flight is never asked who is calling.
    [Fact]
    public async Task AFilesystemCall_OutsideAnyTurn_CarriesNoContext()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new ProbeSettings("probe"))
            .WithTools<MetaEchoingFileSystemTools>());
        var backend = new McpFileSystemBackend(server.Client, "probe", advertisedOperations: null);

        var info = (await backend.InfoAsync("/anything", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;

        info.Path.ShouldBe("nobody");
    }
}

// An fs_info that answers with the caller its `_meta` names, in the path field, so the test reads
// what the server saw through the same typed result the backend always returns.
[McpServerToolType]
public sealed class MetaEchoingFileSystemTools
{
    [McpServerTool(Name = "fs_info")]
    [Description("Answers the caller as the path.")]
    public static string Info(RequestContext<CallToolRequestParams> context, string path, string? filesystem = null)
    {
        var caller = global::Domain.Channels.ConversationScope.Parse(context.Params?.Meta) is { } c
            ? $"{c.AgentId}:{c.ConversationId}"
            : "nobody";
        return new JsonObject
        {
            ["exists"] = true,
            ["isDirectory"] = false,
            ["path"] = caller,
            // Again where a file tool, which echoes its caller's own path, leaves it alone.
            ["lastModified"] = caller
        }.ToJsonString();
    }
}