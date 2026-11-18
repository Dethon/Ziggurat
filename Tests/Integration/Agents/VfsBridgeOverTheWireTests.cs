using System.Text;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Security;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
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
        var call = new VfsBridge(new FakeTimeProvider(), new VfsBridgeSettings())
            .Mint(BridgeFixtures.Registry((new WireMount(backend), "/timers", null)), BridgeFixtures.Everything, null, null);

        var read = await call.ReadAsync("/timers/pasta/status.json", CancellationToken.None);

        Encoding.UTF8.GetString(read.ShouldBeOfType<BridgeAnswer<byte[]>.Ok>().Value)
            .ShouldBe("""{"remainingSeconds":300}""");
    }

    // A mount that answers by who is calling — the Home Assistant watches record their author —
    // sees the conversation the command serves, though the bridge's operation runs on its own
    // request with no turn in flight to read it from.
    [Fact]
    public async Task AWriteThroughTheBridge_ReachesTheServerAsTheConversationItServes()
    {
        var seen = new List<string>();
        await using var server = await InMemoryMcpServer.StartAsync(services =>
        {
            services.AddSingleton(new CallerRecordingMount(seen));
            services.AddToolServer(new ProbeSettings("watches")).AddFileSystemTools<CallerRecordingMount>();
        });
        var backend = new McpFileSystemBackend(
            server.Client, "watches",
            McpFileSystemDiscovery.AdvertisedOperations(FileSystemServerTools.SupportedToolNames(typeof(CallerRecordingMount))));
        var caller = new global::Domain.DTOs.Channel.ConversationContext(
            "jonas", "conv-1", "fran", new global::Domain.DTOs.Channel.ReplyTarget("telegram", "conv-1"));
        // Mounted as discovery mounts it: the proxy itself, not a stand-in.
        var registry = new VirtualFileSystemRegistry();
        registry.Mount(new FileSystemMount("watches", "/watches", "Watches."), backend);
        var call = new VfsBridge(new FakeTimeProvider(), new VfsBridgeSettings()).Mint(registry, BridgeFixtures.Everything, null, caller);

        await call.WriteAsync("/watches/w1/watch.json", "{}"u8.ToArray(), isNew: true, CancellationToken.None);

        seen.ShouldBe(["jonas:conv-1"]);
    }

    // Two filesystems on one server sharing /watchers, mounted as a session mounts them: published,
    // discovered, joined into one union by the real registry. A command sees one directory holding
    // both members' entries, reads each from its holder, and a file it writes goes to the member the
    // content's values are claimed by — as the conversation the command serves.
    [Fact]
    public async Task AUnionMount_IsServedToACommandAsOneDirectory()
    {
        var seen = new List<string>();
        await using var server = await InMemoryMcpServer.StartAsync(services =>
        {
            services.AddSingleton(new HomeWatches(seen));
            services.AddSingleton(new GlucoseWatches(seen));
            services.AddToolServer(new ProbeSettings("watches"))
                .AddFileSystemTools<HomeWatches>()
                .AddFileSystemResource<HomeWatches>()
                .AddFileSystemTools<GlucoseWatches>()
                .AddFileSystemResource<GlucoseWatches>();
        });
        var registry = new VirtualFileSystemRegistry();
        var shadowed = await McpFileSystemDiscovery.DiscoverAndMountAsync(
            [server.Client], new HashSet<ModelContextProtocol.Client.McpClient>(), registry,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        var caller = new global::Domain.DTOs.Channel.ConversationContext(
            "jonas", "conv-1", "fran", new global::Domain.DTOs.Channel.ReplyTarget("telegram", "conv-1"));
        var call = new VfsBridge(new FakeTimeProvider(), new VfsBridgeSettings()).Mint(registry, BridgeFixtures.Everything, null, caller);
        var ct = CancellationToken.None;

        shadowed.ShouldBeEmpty();
        registry.GetMounts().ShouldHaveSingleItem().MountPoint.ShouldBe("/watchers");
        call.ServedNames.ShouldBe(["watchers"]);

        var listing = (await call.ListAsync("/watchers", ct)).ShouldBeOfType<BridgeAnswer<BridgeListing>.Ok>().Value;
        listing.Entries.Select(e => e.Name).ShouldBe(["low", "porch"], ignoreOrder: true);
        listing.Truncated.ShouldBeFalse();

        Encoding.UTF8.GetString((await call.ReadAsync("/watchers/low/watch.json", ct)).ShouldBeOfType<BridgeAnswer<byte[]>.Ok>().Value)
            .ShouldContain("glucose");
        Encoding.UTF8.GetString((await call.ReadAsync("/watchers/porch/watch.json", ct)).ShouldBeOfType<BridgeAnswer<byte[]>.Ok>().Value)
            .ShouldContain("state");

        (await call.WriteAsync("/watchers/high/watch.json", """{"triggers":[{"trigger":"glucose"}]}"""u8.ToArray(), isNew: true, ct))
            .ShouldBeOfType<BridgeAnswer<bool>.Ok>();
        (await call.WriteAsync("/watchers/door/watch.json", """{"triggers":[{"trigger":"state"}]}"""u8.ToArray(), isNew: true, ct))
            .ShouldBeOfType<BridgeAnswer<bool>.Ok>();

        seen.ShouldBe(["sugary-watches:high/watch.json:conv-1", "ha-watches:door/watch.json:conv-1"]);
    }

    private abstract class WatchStore(string name, Dictionary<string, string> files, List<string> seen, FileSystemCaller? caller)
        : FileSystemBackendBase
    {
        public override string FilesystemName => name;

        public override string MountPoint => "/watchers";

        public override string DescribeMount => $"The {name}.";

        public override Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct) =>
            Task.FromResult(Glob([.. files.Keys.Select(k => k.Split('/')[0] + "/").Distinct()]));

        public override Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(path, out var content)
                ? new FsResult<FsReadResult>.Ok(new FsReadResult { FilePath = path, Content = content, TotalLines = 1, Truncated = false })
                : NotFound<FsReadResult>(path));

        public override Task<FsResult<FsCreateResult>> CreateAsync(
            string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
        {
            lock (seen)
            {
                seen.Add($"{name}:{path}:{caller?.Conversation?.ConversationId ?? "nobody"}");
            }

            return Task.FromResult<FsResult<FsCreateResult>>(new FsResult<FsCreateResult>.Ok(new FsCreateResult
            {
                Status = "created", FilePath = path, Size = "2B", Lines = 1
            }));
        }
    }

    private sealed class HomeWatches(List<string> seen, FileSystemCaller? caller = null) : WatchStore(
        "ha-watches", new Dictionary<string, string> { ["porch/watch.json"] = """{"triggers":[{"trigger":"state"}]}""" }, seen, caller)
    {
        public override FileSystemUnion Union => new("/triggers/*/trigger", [], Fallback: true);

        public override FileSystemBackendBase For(FileSystemCaller caller) => new HomeWatches(seen, caller);
    }

    private sealed class GlucoseWatches(List<string> seen, FileSystemCaller? caller = null) : WatchStore(
        "sugary-watches", new Dictionary<string, string> { ["low/watch.json"] = """{"triggers":[{"trigger":"glucose"}]}""" }, seen, caller)
    {
        public override FileSystemUnion Union => new("/triggers/*/trigger", ["glucose", "glucose_trend"]);

        public override FileSystemBackendBase For(FileSystemCaller caller) => new GlucoseWatches(seen, caller);
    }

    private sealed class CallerRecordingMount(List<string> seen, FileSystemCaller? caller = null) : FileSystemBackendBase
    {
        public override string FilesystemName => "watches";

        public override string DescribeMount => "Remembers who wrote.";

        public override FileSystemBackendBase For(FileSystemCaller caller) => new CallerRecordingMount(seen, caller);

        public override Task<FsResult<FsCreateResult>> CreateAsync(
            string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
        {
            seen.Add(caller?.Conversation is { } c ? $"{c.AgentId}:{c.ConversationId}" : "nobody");
            return Task.FromResult<FsResult<FsCreateResult>>(new FsResult<FsCreateResult>.Ok(new FsCreateResult
            {
                Status = "created", FilePath = path, Size = "2B", Lines = 1
            }));
        }
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

        public override Task<FsResult<FsCreateResult>> CreateAsync(
            string path, string content, bool overwrite, bool createDirectories, CancellationToken ct) =>
            wire.CreateAsync(path, content, overwrite, createDirectories, ct);
    }
}