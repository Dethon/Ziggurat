using System.Text;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Security;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents.Mcp;
using Infrastructure.Utils;
using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.McpServers;
using Tests.Unit.Domain.Tools.FileSystem.Bridge;

namespace Tests.Integration.Agents;

// The bridge against a mount the way the agent really holds one: a server across the MCP wire,
// advertising exactly what its backend overrides. A rendered mount advertises no blob read, and
// its status files are text it renders; a read through the shell has to come back as that text,
// never as the wire's answer for a tool the server does not have.
public class VfsBridgeOverTheWireTests
{
    private sealed record ProbeSettings(string Name) : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    [Fact]
    public async Task ARenderedFileOnAServerWithNoBlobRead_IsReadAsTheTextItRenders()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services =>
        {
            services.AddSingleton(new RenderedMount("timers", new Dictionary<string, string>
            {
                ["pasta/status.json"] = """{"remainingSeconds":300}"""
            }));
            services.AddToolServer(new ProbeSettings("timers")).AddFileSystemTools<RenderedMount>();
        });
        var backend = new McpFileSystemBackend(
            server.Client, "timers",
            McpFileSystemDiscovery.AdvertisedOperations(FileSystemServerTools.SupportedToolNames(typeof(RenderedMount))));
        var call = new VfsBridge(new FakeTimeProvider())
            .Mint(BridgeFixtures.Registry((new WireMount(backend), "/timers", null)), _ => true);

        var read = await call.ReadAsync("/timers/pasta/status.json", CancellationToken.None);

        Encoding.UTF8.GetString(read.ShouldBeOfType<BridgeAnswer<byte[]>.Ok>().Value)
            .ShouldBe("""{"remainingSeconds":300}""");
    }

    // The registry holds IFileSystemBackend; the fixture's helper takes the base type, so the wire
    // proxy is wrapped in a pass-through that forwards the operations the bridge uses.
    private sealed class WireMount(McpFileSystemBackend wire) : global::Domain.Contracts.FileSystemBackendBase
    {
        public override string FilesystemName => wire.FilesystemName;

        public override string DescribeMount => "Over the wire.";

        public override Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
            wire.ReadAsync(path, offset, limit, ct);

        public override IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct) =>
            wire.ReadChunksAsync(path, ct);
    }
}