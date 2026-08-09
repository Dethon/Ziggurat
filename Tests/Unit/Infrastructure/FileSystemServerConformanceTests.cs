using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Domain.Tools.HomeAssistant.Vfs;
using Domain.Tools.Printing;
using Domain.Tools.Printing.Vfs;
using Domain.Tools.Scheduling.Vfs;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Agents.Mcp;
using Infrastructure.Utils;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Moq;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Unit.Infrastructure;

// The point of the whole feature: for every filesystem server, the fs_* tools it advertises, the
// operations its backend overrides, and the capabilities the mount publishes are the same set.
// A server that registered a tool its backend does not implement fails here — which is how the
// timers move lie was found: fs_move was advertised by a method that only said "unsupported".
public class FileSystemServerConformanceTests
{
    // Mount name, the id of the server that publishes it in the one server table, and the backend
    // behind it.
    public static TheoryData<string, string, Type> Backends() => new()
    {
        { "timers", "timers", typeof(TimerFileSystem) },
        { "schedules", "scheduling", typeof(ScheduleFileSystem) },
        { "print-queue", "printer", typeof(PrinterQueueFileSystem) },
        { "ha", "homeassistant", typeof(HaFileSystem) },
        { "media", "library", typeof(MediaLibraryDiskFileSystem) },
        { "vault", "vault", typeof(TextDiskFileSystem) },
        { "sandbox", "sandbox", typeof(SandboxFileSystem) }
    };

    // What each mount is expected to advertise, written out rather than derived. Everything else in
    // this file is compared against these, so the registrar's own reflection is never both the code
    // under test and the yardstick — which is what a `registered.ShouldBe(overridden.Count)` was,
    // and it stayed green for a server that had dropped AddFileSystemTools<T>() altogether.
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _advertised =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            // No fs_edit: a timer is immutable, so every branch of an edit could only fail, and an
            // override that can never succeed is the drift declaring-by-overriding removes.
            ["timers"] = ["fs_read", "fs_info", "fs_glob", "fs_search", "fs_create", "fs_delete", "fs_exec"],
            ["schedules"] =
                ["fs_read", "fs_info", "fs_glob", "fs_search", "fs_create", "fs_edit", "fs_move", "fs_delete", "fs_exec"],
            ["print-queue"] =
            [
                "fs_read", "fs_info", "fs_glob", "fs_search", "fs_create", "fs_edit",
                "fs_delete", "fs_copy", "fs_blob_read", "fs_blob_write"
            ],
            ["ha"] = ["fs_read", "fs_info", "fs_glob", "fs_search", "fs_exec"],
            // The media library reads only the overlay's status file and writes no text, so it keeps
            // the plain disk surface plus read. It is also the one mount with a move-out rule, and
            // the only one that registers the check.
            ["media"] =
            [
                "fs_read", "fs_info", "fs_glob", "fs_move", "fs_delete", "fs_copy",
                "fs_blob_read", "fs_blob_write", "fs_move_out_check"
            ],
            ["vault"] =
            [
                "fs_read", "fs_info", "fs_glob", "fs_search", "fs_create", "fs_edit", "fs_move",
                "fs_delete", "fs_copy", "fs_blob_read", "fs_blob_write"
            ],
            // Exec is the one thing the sandbox has and the vault does not.
            ["sandbox"] =
            [
                "fs_read", "fs_info", "fs_glob", "fs_search", "fs_create", "fs_edit", "fs_move",
                "fs_delete", "fs_copy", "fs_exec", "fs_blob_read", "fs_blob_write"
            ]
        };

    // The shipped server, not a re-registration of it: each row drives the ConfigModule that runs in
    // production, so a module that never called AddFileSystemTools<T>() or AddFileSystemResource<T>()
    // fails here. Hand-registering the registrar instead — which this test used to do — can only
    // ever assert that the registrar agrees with itself.
    [Theory]
    [MemberData(nameof(Backends))]
    public void EveryFilesystemServer_RegistersTheToolsAndTheMountItAdvertises(
        string name, string serverId, Type backendType)
    {
        using var provider = ConfiguredServer(serverId);

        var registered = provider.GetServices<McpServerTool>()
            .Select(tool => tool.ProtocolTool.Name)
            .Where(FileSystemOperations.ToolNames.Contains)
            .ToList();

        registered.ShouldBe(_advertised[name], ignoreOrder: true, serverId);

        var mount = provider.GetServices<McpServerResource>()
            .SingleOrDefault(resource => resource.ProtocolResource?.Uri == $"filesystem://{name}");
        mount.ShouldNotBeNull($"{serverId} must publish its {name} mount");
        mount.ProtocolResource!.Name.ShouldBe(name);

        // The backend's own declaration of the same set: what it overrides is what the server
        // registers is what the mount publishes.
        FileSystemServerTools.SupportedToolNames(backendType)
            .ShouldBe(_advertised[name], ignoreOrder: true, serverId);

        // What the mount publishes to the model: every advertised operation the model can call, and
        // nothing else. The two blob tools are transfer machinery, not model-facing.
        McpFileSystemDiscovery.DeriveCapabilities(McpFileSystemDiscovery.AdvertisedOperations(registered)).ShouldBe(
            FileSystemOperations.All
                .Where(o => o.Capability is not null && _advertised[name].Contains(o.ToolName))
                .Select(o => o.Capability!),
            serverId);
    }

    private static ServiceProvider ConfiguredServer(string serverId)
    {
        var services = new ServiceCollection();
        McpServerRegistrations.Get(serverId).Configure(services);
        return services.BuildServiceProvider();
    }

    // Every filesystem backend in the repo, constructed. The tool assertions only need the type,
    // but a mount's identity is a value the instance carries, so the identity assertion holds the
    // real thing — a backend whose FilesystemName drifted from the mount it is published at fails.
    private static IReadOnlyDictionary<string, FileSystemBackendBase> MountedBackends =>
        new Dictionary<string, FileSystemBackendBase>
        {
            ["timers"] = new TimerFileSystem(
                Mock.Of<ITimerStore>(), TimeProvider.System, Mock.Of<IAlertDismisser>(),
                Mock.Of<ISatelliteCatalog>()),
            ["schedules"] = new ScheduleFileSystem(
                Mock.Of<IScheduleStore>(), Mock.Of<IAgentCatalog>(), Mock.Of<ICronValidator>(),
                TimeProvider.System),
            ["print-queue"] = new PrinterQueueFileSystem(
                Mock.Of<IPrintSpool>(), Mock.Of<IPrinterClient>(), new PrintQueueGate(), "text,jpeg"),
            ["ha"] = new HaFileSystem(
                new HaCatalogProvider(Mock.Of<IHomeAssistantClient>), Mock.Of<IHomeAssistantClient>),
            ["media"] = new MediaLibraryDiskFileSystem(
                Mock.Of<IFileSystemClient>(), new LibraryPathConfig("/media"),
                new DownloadsOverlay(
                    Mock.Of<IDownloadClient>(), Mock.Of<IDownloadRoutingStore>(),
                    Mock.Of<IFileSystemClient>(), new LibraryPathConfig("/media"))),
            ["vault"] = new TextDiskFileSystem(
                "vault", "A personal vault.", Mock.Of<IFileSystemClient>(),
                new LibraryPathConfig("/vault"), [".md"]),
            ["sandbox"] = new SandboxFileSystem(
                "sandbox", "A sandbox container.", Mock.Of<IFileSystemClient>(),
                new LibraryPathConfig("/sandbox"), [".py"], Mock.Of<ICommandRunner>())
        };

    // The other half of the same idea. A mount's identity used to be written three times per server
    // — in the backend, in the resource address, and in the resource body's name and mount point —
    // and nothing compared the three. Now all three come off the backend's one name, so a mount the
    // agent discovered at an address is a mount it can address.
    [Theory]
    [MemberData(nameof(Backends))]
    public void EveryFilesystemServer_PublishesItsMountAtTheAddressDerivedFromItsName(
        string name, string serverId, Type backendType)
    {
        var backend = MountedBackends[name];
        backend.ShouldBeOfType(backendType);

        var services = new ServiceCollection();
        typeof(FileSystemServerResource)
            .GetMethod(nameof(FileSystemServerResource.AddFileSystemResource))!
            .MakeGenericMethod(backendType)
            .Invoke(null, [services.AddMcpServer()]);

        services.Single(d => d.ServiceType == typeof(McpServerResource))
            .Lifetime.ShouldBe(ServiceLifetime.Singleton, name);

        backend.FilesystemName.ShouldBe(name, serverId);
        FileSystemServerResource.Address(backend.FilesystemName).ShouldBe($"filesystem://{name}");

        var published = Published(FileSystemServerResource.Describe(backend));
        published.Name.ShouldBe(name);
        published.MountPoint.ShouldBe($"/{name}");
        published.Description.ShouldBe(backend.DescribeMount);
        published.Description.ShouldNotBeNullOrWhiteSpace(name);
    }

    // That the resource the registrar really builds carries the same three, so nothing between the
    // backend and the wire re-derives them.
    [Fact]
    public void TheRegisteredResource_TakesItsAddressAndBodyFromTheBackend()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new PickyBackend());
        services.AddMcpServer().AddFileSystemResource<PickyBackend>();
        using var provider = services.BuildServiceProvider();

        var resource = provider.GetServices<McpServerResource>().Single();

        resource.ProtocolResource!.Uri.ShouldBe("filesystem://picky");
        resource.ProtocolResource.Name.ShouldBe("picky");
        resource.ProtocolResource.Description.ShouldBe(new PickyBackend().DescribeMount);
        resource.ProtocolResource.MimeType.ShouldBe("application/json");

    }

    // Read back the way McpFileSystemDiscovery reads it, so the body this test approves is the body
    // the agent's mount actually parses.
    private static PublishedMount Published(string json) =>
        JsonSerializer.Deserialize<PublishedMount>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private record PublishedMount(string Name, string MountPoint, string Description);

    // The lie this feature removes: timers registered fs_move from a method whose own description
    // said the operation was unsupported, so the prompt promised the model an operation that could
    // only fail. Nothing overrides move now, so nothing can advertise it.
    [Theory]
    [InlineData(typeof(TimerFileSystem))]
    [InlineData(typeof(PrinterQueueFileSystem))]
    public void AMountThatNeverImplementedMove_DoesNotAdvertiseIt(Type backendType)
    {
        var advertised = FileSystemServerTools.SupportedToolNames(backendType);

        advertised.ShouldNotContain("fs_move");
        McpFileSystemDiscovery.DeriveCapabilities(McpFileSystemDiscovery.AdvertisedOperations(advertised))
            .ShouldNotContain("move");
    }

    // The move-out check inverts what an override means: elsewhere overriding declares "I can do
    // this", here it declares "I have something to refuse". So a backend with no rule registers no
    // tool, and the base default answers every path as allowed — which is why adding the check
    // touched no other backend.
    [Fact]
    public async Task ABackendWithNoMoveOutRule_RegistersNoCheckAndAllowsEveryPath()
    {
        FileSystemServerTools.SupportedToolNames(typeof(PickyBackend))
            .ShouldNotContain("fs_move_out_check");

        (await new PickyBackend().MoveOutCheckAsync("anything", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveOutCheckResult>.Ok>();
    }

    // The other half: the media library has a rule, so it registers the tool with its own prose.
    [Fact]
    public void TheOneBackendWithAMoveOutRule_RegistersTheCheckWithItsOwnDescription()
    {
        var backend = MountedBackends["media"];

        FileSystemServerTools.SupportedToolNames(typeof(MediaLibraryDiskFileSystem))
            .ShouldContain("fs_move_out_check");
        backend.DescribeMoveOutCheck.ShouldNotBe(new PickyBackend().DescribeMoveOutCheck);
    }

    [Fact]
    public void RegisteredTools_CarryTheBackendsDescriptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new PickyBackend());
        services.AddMcpServer().AddFileSystemTools<PickyBackend>();
        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().ToList();

        tools.Select(t => t.ProtocolTool.Name).ShouldBe(["fs_read"]);
        tools.Single().ProtocolTool.Description.ShouldBe(new PickyBackend().DescribeRead);
    }

    // The two blob operations have two shapes — the chunk stream the transfer machinery drives and
    // the ranged pair the wire carries — and the base invites a backend with real random access to
    // override the ranged pair beside, or instead of, the streamed one. Capability was keyed on the
    // chunk methods alone, so such a backend advertised no fs_blob_read at all while the registrar
    // dispatched fs_blob_read to exactly the method it had overridden.
    [Fact]
    public void ABackendThatOverridesOnlyTheRangedBlobPair_StillAdvertisesTheBlobTools()
    {
        FileSystemServerTools.SupportedToolNames(typeof(RangedBlobBackend))
            .ShouldBe(["fs_blob_read", "fs_blob_write"], ignoreOrder: true);
    }

    // The other direction, and the one that could ship a lie: the wire dispatches fs_blob_write to
    // the ranged method, and the streamed default underneath it refuses every nonzero offset while
    // the transfer driver sends one per 256 KiB chunk. So a backend that only streams bytes serves
    // reads fine — the read default replays the stream — and advertises no ranged write at all,
    // rather than one that works only for files smaller than a single chunk.
    [Fact]
    public void ABackendThatOnlyStreamsBytes_AdvertisesTheBlobReadItCanServeAndNoBlobWrite()
    {
        FileSystemServerTools.SupportedToolNames(typeof(StreamingBlobBackend)).ShouldBe(["fs_blob_read"]);
    }

    private sealed class StreamingBlobBackend : FileSystemBackendBase
    {
        public override string FilesystemName => "streamed";

        public override string DescribeMount => "Bytes, forward-only.";

        public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
            string path, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new byte[] { 1, 2, 3 };
        }

        public override Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
            bool overwrite, bool createDirectories, CancellationToken ct) =>
            Task.FromResult(0L);
    }

    private sealed class RangedBlobBackend : FileSystemBackendBase
    {
        public override string FilesystemName => "ranged";

        public override string DescribeMount => "Bytes, addressed by range.";

        public override Task<FsResult<FsBlobReadResult>> ReadBlobAsync(
            string path, long offset, int length, CancellationToken ct) =>
            Task.FromResult<FsResult<FsBlobReadResult>>(new FsResult<FsBlobReadResult>.Ok(
                new FsBlobReadResult { ContentBase64 = "", Eof = true, TotalBytes = 0 }));

        public override Task<FsResult<FsBlobWriteResult>> WriteBlobAsync(
            string path, string contentBase64, long offset, bool overwrite, bool createDirectories,
            CancellationToken ct) =>
            Task.FromResult<FsResult<FsBlobWriteResult>>(new FsResult<FsBlobWriteResult>.Ok(
                new FsBlobWriteResult { Path = path, BytesWritten = 0, TotalBytes = 0 }));
    }

    private sealed class PickyBackend : FileSystemBackendBase
    {
        public override string FilesystemName => "picky";

        public override string DescribeRead => "Reads only the one file this mount is willing to serve.";

        public override string DescribeMount => "One file, and only if you ask nicely.";

        public override Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
            Task.FromResult(path == "allowed.md"
                ? new FsResult<FsReadResult>.Ok(new FsReadResult
                {
                    FilePath = path, Content = "", TotalLines = 0, Truncated = false
                })
                : NotFound<FsReadResult>(path));
    }
}