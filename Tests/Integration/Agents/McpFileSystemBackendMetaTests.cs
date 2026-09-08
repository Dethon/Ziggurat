using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
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
// backend stamps the turn's conversation context the way a directly-called tool does.
public class McpFileSystemBackendMetaTests
{
    private sealed record ProbeSettings(string Name);

    private static readonly PropertyInfo _currentContext =
        typeof(FunctionInvokingChatClient).GetProperty("CurrentContext", BindingFlags.Public | BindingFlags.Static)!;

    [Fact]
    public async Task AFilesystemCall_MadeInsideATurn_CarriesTheConversationContextToTheServer()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new ProbeSettings("probe"))
            .WithTools<MetaEchoingFileSystemTools>());
        var backend = new McpFileSystemBackend(server.Client, "probe", advertisedOperations: null);
        var context = new ConversationContext("jonas", "conv-1", "fran", new ReplyTarget("telegram", "conv-1"));
        _currentContext.SetValue(null, new FunctionInvocationContext
        {
            Options = new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { [ConversationContextMeta.OptionsKey] = context }
            }
        });

        try
        {
            var info = (await backend.InfoAsync("/anything", CancellationToken.None))
                .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;

            info.Path.ShouldBe("jonas:conv-1");
        }
        finally
        {
            _currentContext.SetValue(null, null);
        }
    }

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

// An fs_info that answers with the caller the filter entered, in the path field, so the test reads
// what the server saw through the same typed result the backend always returns.
[McpServerToolType]
public sealed class MetaEchoingFileSystemTools
{
    [McpServerTool(Name = "fs_info")]
    [Description("Answers the caller as the path.")]
    public static string Info(string path, string? filesystem = null) =>
        new JsonObject
        {
            ["exists"] = true,
            ["isDirectory"] = false,
            ["path"] = global::Domain.Channels.CallerContext.Current is { } caller ? $"{caller.AgentId}:{caller.ConversationId}" : "nobody"
        }.ToJsonString();
}