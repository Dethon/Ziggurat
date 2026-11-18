using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Security;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Infrastructure.Utils;
using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Tests.Integration.McpServers;

// One server may serve several filesystems — Home Assistant its home and its watches, Sugary her
// log and hers. Tools are per operation, so the server registers one fs_* tool per operation any
// of its filesystems overrides, and each call says which filesystem it is for. A call that names
// none reaches the first, which is how an outpost or a kit server from before keeps working.
public class SeveralFileSystemsPerServerTests
{
    private sealed record ProbeSettings(string Name) : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    private static Task<RunningServer> StartAsync() => InMemoryMcpServer.StartAsync(services =>
    {
        services.AddSingleton<AlphaFileSystem>();
        services.AddSingleton<BetaFileSystem>();
        services.AddToolServer(new ProbeSettings("probe"))
            .AddFileSystemTools<AlphaFileSystem>()
            .AddFileSystemResource<AlphaFileSystem>()
            .AddFileSystemTools<BetaFileSystem>()
            .AddFileSystemResource<BetaFileSystem>();
    });

    private static Task<CallToolResult> CallAsync(
        RunningServer server, string tool, string? filesystem, params (string Name, object? Value)[] args)
    {
        var arguments = args.ToDictionary(a => a.Name, a => JsonSerializer.SerializeToElement(a.Value));
        if (filesystem is not null)
        {
            arguments["filesystem"] = JsonSerializer.SerializeToElement(filesystem);
        }

        return server.Client.CallToolAsync(new CallToolRequestParams { Name = tool, Arguments = arguments }).AsTask();
    }

    private static JsonNode Body(CallToolResult result) => JsonNode.Parse(InMemoryMcpServer.Text(result))!;

    [Fact]
    public async Task TheServer_AdvertisesTheUnionOfItsFilesystemsOperations()
    {
        await using var server = await StartAsync();

        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        tools.ShouldBe(
            ["fs_read", "fs_info", "fs_move_out_check", "fs_create", "fs_blob_read"], ignoreOrder: true);
    }

    // The agent filters raw fs_* tools out, so a shared tool's prose only has to be one of them:
    // the first filesystem's.
    [Fact]
    public async Task ASharedTool_TakesTheFirstFilesystemsDescription()
    {
        await using var server = await StartAsync();

        var info = (await server.Client.ListToolsAsync()).Single(t => t.Name == "fs_info");

        info.Description.ShouldBe(new AlphaFileSystem().DescribeInfo);
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("beta")]
    public async Task ACall_ReachesTheFilesystemItNames(string filesystem)
    {
        await using var server = await StartAsync();

        var result = await CallAsync(server, "fs_info", filesystem, ("path", "/x"));

        Body(result)["path"]!.GetValue<string>().ShouldBe($"{filesystem}:/x");
    }

    [Fact]
    public async Task ACallNamingNoFilesystem_ReachesTheFirstRegistered()
    {
        await using var server = await StartAsync();

        var result = await CallAsync(server, "fs_info", null, ("path", "/x"));

        Body(result)["path"]!.GetValue<string>().ShouldBe("alpha:/x");
    }

    [Fact]
    public async Task ACallNamingAFilesystemTheServerDoesNotServe_IsRefusedNamingTheOnesItDoes()
    {
        await using var server = await StartAsync();

        var result = await CallAsync(server, "fs_info", "gamma", ("path", "/x"));

        result.IsError.ShouldBe(true);
        var body = Body(result);
        body["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
        var message = body["message"]!.GetValue<string>();
        message.ShouldContain("gamma");
        message.ShouldContain("alpha");
        message.ShouldContain("beta");
    }

    [Theory]
    [InlineData("fs_read", "beta")]
    [InlineData("fs_create", "alpha")]
    [InlineData("fs_blob_read", "alpha")]
    public async Task AnOperationTheNamedFilesystemDoesNotOverride_IsUnsupported(string tool, string filesystem)
    {
        await using var server = await StartAsync();

        var result = await CallAsync(server, tool, filesystem, ("path", "/x"), ("content", "c"));

        result.IsError.ShouldBe(true);
        Body(result)["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
    }

    // The move-out check is the one operation whose default allows: a filesystem with no rule has
    // nothing to refuse, so the check another filesystem on the server registered answers it as
    // allowed rather than unsupported (ADR 0015).
    [Fact]
    public async Task TheMoveOutCheck_OnAFilesystemWithNoRule_Allows()
    {
        await using var server = await StartAsync();

        (await CallAsync(server, "fs_move_out_check", "alpha", ("path", "/x"))).IsError.ShouldBe(true);
        (await CallAsync(server, "fs_move_out_check", "beta", ("path", "/x"))).IsError.ShouldNotBe(true);
    }

    [Fact]
    public async Task EachFilesystemsResource_PublishesItsOwnOperations()
    {
        await using var server = await StartAsync();

        (await OperationsPublishedAt(server, "filesystem://alpha"))
            .ShouldBe(["fs_read", "fs_info", "fs_move_out_check"], ignoreOrder: true);
        (await OperationsPublishedAt(server, "filesystem://beta"))
            .ShouldBe(["fs_create", "fs_info", "fs_blob_read"], ignoreOrder: true);
    }

    private static async Task<IReadOnlyList<string>> OperationsPublishedAt(RunningServer server, string uri)
    {
        var read = await server.Client.ReadResourceAsync(uri);
        var body = JsonNode.Parse(read.Contents.OfType<TextResourceContents>().Single().Text)!;
        return [.. body["operations"]!.AsArray().Select(o => o!.GetValue<string>())];
    }

    [Fact]
    public async Task Discovery_MountsEachFilesystemWithItsOwnOperations()
    {
        await using var server = await StartAsync();
        var registry = new VirtualFileSystemRegistry();

        await McpFileSystemDiscovery.DiscoverAndMountAsync(
            [server.Client], new HashSet<McpClient>(), registry, NullLogger.Instance, CancellationToken.None);

        var mounts = registry.GetMounts().ToDictionary(m => m.Name);
        mounts["alpha"].Capabilities.ShouldBe([VfsFileReadTool.Name, VfsFileInfoTool.Name]);
        mounts["beta"].Capabilities.ShouldBe([VfsTextCreateTool.Name, VfsFileInfoTool.Name]);

        // The proxy is told its own filesystem's operations too, not the server's: alpha has no
        // byte read, so its stream is refused here rather than asked of the wire.
        var alpha = registry.Resolve("/alpha/x").ShouldBeOfType<FsResult<FileSystemResolution>.Ok>().Value.Backend;
        Should.Throw<NotSupportedException>(() => alpha.ReadChunksAsync("x", CancellationToken.None));
        var beta = registry.Resolve("/beta/x").ShouldBeOfType<FsResult<FileSystemResolution>.Ok>().Value.Backend;
        await foreach (var chunk in beta.ReadChunksAsync("x", CancellationToken.None))
        {
            chunk.ToArray().ShouldBe([7]);
        }
    }
}

public sealed class AlphaFileSystem : FileSystemBackendBase
{
    public override string FilesystemName => "alpha";

    public override string DescribeMount => "The first filesystem.";

    public override string DescribeInfo => "Alpha's info.";

    public override Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        Task.FromResult<FsResult<FsInfoResult>>(new FsResult<FsInfoResult>.Ok(
            new FsInfoResult { Exists = true, Path = $"alpha:{path}" }));

    public override Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
        Task.FromResult(NotFound<FsReadResult>(path));

    public override Task<FsResult<FsMoveOutCheckResult>> MoveOutCheckAsync(string path, CancellationToken ct) =>
        Task.FromResult(Invalid<FsMoveOutCheckResult>("nothing leaves alpha"));
}

public sealed class BetaFileSystem : FileSystemBackendBase
{
    public override string FilesystemName => "beta";

    public override string DescribeMount => "The second filesystem.";

    public override string DescribeInfo => "Beta's info.";

    public override Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        Task.FromResult<FsResult<FsInfoResult>>(new FsResult<FsInfoResult>.Ok(
            new FsInfoResult { Exists = true, Path = $"beta:{path}" }));

    public override Task<FsResult<FsCreateResult>> CreateAsync(
        string path, string content, bool overwrite, bool createDirectories, CancellationToken ct) =>
        Task.FromResult(Invalid<FsCreateResult>("beta writes nothing in this test"));

    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield return new byte[] { 7 };
    }
}