using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Infrastructure.Utils;
using Mcp.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Tests.Integration.McpServers;

// A filesystem backend's operations take a path and a payload, never the request, and one mount
// has to know who is calling: the Home Assistant watches record the agent that created them. The
// registrar reads the call's `_meta` and asks the backend for itself as that caller sees it
// (`FileSystemBackendBase.For`), so the caller arrives as plain data — no ambient to enter, and a
// backend that does not care answers itself.
public class FileSystemCallerTests
{
    private sealed record ProbeSettings(string Name);

    private static Task<RunningServer> StartAsync() => InMemoryMcpServer.StartAsync(services =>
    {
        services.AddSingleton<CallerEchoFileSystem>();
        services.AddToolServer(new ProbeSettings("probe")).AddFileSystemTools<CallerEchoFileSystem>();
    });

    [Fact]
    public async Task ACallCarryingAConversationContext_ReachesTheBackendAsItsCaller()
    {
        await using var server = await StartAsync();
        var context = new ConversationContext("jonas", "conv-1", "fran", new ReplyTarget("telegram", "conv-1"));
        var tool = (await server.Client.ListToolsAsync()).Single(t => t.Name == "fs_info")
            .WithMeta(new JsonObject
            {
                [ChannelProtocol.ConversationContextMetaKey] = JsonSerializer.SerializeToNode(context, ChannelProtocol.SerializerOptions)
            });

        var result = await tool.InvokeAsync(new AIFunctionArguments { ["path"] = "/anything" }, CancellationToken.None);

        JsonSerializer.Serialize(result).ShouldContain("jonas:conv-1");
    }

    [Fact]
    public async Task ACallWithoutAContext_ReachesTheBackendWithNoCaller()
    {
        await using var server = await StartAsync();

        var result = await server.Client.CallToolAsync(
            "fs_info", new Dictionary<string, object?> { ["path"] = "/anything" });

        InMemoryMcpServer.Text(result).ShouldContain("nobody");
    }

    // A context that does not parse is answered in the standard error envelope every other failure
    // gets — a code the model can act on — rather than escaping for the SDK to wrap in its own words.
    [Fact]
    public async Task ACallWhoseContextDoesNotParse_IsTheStandardErrorEnvelope()
    {
        await using var server = await StartAsync();

        var result = await server.Client.CallToolAsync(new CallToolRequestParams
        {
            Name = "fs_info",
            Arguments = new Dictionary<string, JsonElement> { ["path"] = JsonSerializer.SerializeToElement("/anything") },
            Meta = new JsonObject { [ChannelProtocol.ConversationContextMetaKey] = "not a context" }
        });

        result.IsError.ShouldBe(true);
        JsonNode.Parse(InMemoryMcpServer.Text(result))!["errorCode"]!.GetValue<string>()
            .ShouldBe(ToolError.Codes.InternalError);
    }
}

// Answers the caller it was asked as, in the path field, so the test reads what the backend saw
// through the same typed result every mount returns.
public sealed class CallerEchoFileSystem(ConversationContext? caller = null) : FileSystemBackendBase
{
    public override string FilesystemName => "probe";

    public override string DescribeMount => "Echoes its caller.";

    public override FileSystemBackendBase For(ConversationContext? caller) => new CallerEchoFileSystem(caller);

    public override Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        Task.FromResult<FsResult<FsInfoResult>>(new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = true,
            Path = caller is null ? "nobody" : $"{caller.AgentId}:{caller.ConversationId}"
        }));
}