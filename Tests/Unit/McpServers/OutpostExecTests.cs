using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Files;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Clients;
using Infrastructure.Clients.Bash;
using Infrastructure.Utils;
using McpServerOutpost.Modules;
using McpServerOutpost.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Shouldly;

namespace Tests.Unit.McpServers;

// Exposing somebody's files must not imply exposing a shell on their computer, so exec is off
// unless its operator asked. The trap this is built around: a backend advertises an operation by
// overriding it and the registrar reflects over the type, so a constructor argument could never
// switch exec off — the outpost registers one of two backend types instead.
public sealed class OutpostExecTests : IDisposable
{
    private const string Mount = "/laptop";

    private readonly string _machine = Directory.CreateTempSubdirectory("outpost-exec-").FullName;
    private readonly string _workingDirectory;

    public OutpostExecTests()
    {
        _workingDirectory = Path.Combine(_machine, "project");
        Directory.CreateDirectory(_workingDirectory);
    }

    public void Dispose() => Directory.Delete(_machine, recursive: true);

    [Fact]
    public void WithExecOff_TheModelIsNotOfferedTheToolAtAll()
    {
        RegisteredFileSystemTools(exec: false).ShouldNotContain("fs_exec");
    }

    [Fact]
    public void WithExecOn_TheServerAdvertisesIt()
    {
        RegisteredFileSystemTools(exec: true).ShouldContain("fs_exec");
    }

    // The mount point means this outpost's working directory rather than the machine's root: an
    // outpost's root is somebody's whole computer, and a command landing there because the caller
    // named no directory is not what its operator asked for.
    [Fact]
    public async Task ACommandAtTheMountPoint_RunsInTheWorkingDirectory()
    {
        var result = await new VfsExecTool(Registry(jailed: false)).RunAsync(Mount, "pwd");

        result.ShouldBeOk()["stdout"]!.GetValue<string>().Trim().ShouldBe(Physical(_workingDirectory));
    }

    // Answered in virtual coordinates like any other path the caller did not name.
    [Fact]
    public async Task TheReportedWorkingDirectory_IsAVirtualPath()
    {
        var result = await new VfsExecTool(Registry(jailed: false)).RunAsync(Mount, "true");

        result.ShouldBeOk()["cwd"]!.GetValue<string>().ShouldBe(Mount + _workingDirectory);
    }

    [Fact]
    public async Task AJailedExecOutpost_RefusesToRunOutsideItsWorkingDirectory()
    {
        var result = await new VfsExecTool(Registry(jailed: true)).RunAsync(Mount + _machine, "pwd");

        result.ShouldBeError(ToolError.Codes.InvalidArgument);
    }

    [Fact]
    public async Task AnUnjailedExecOutpost_RunsWhereverItIsPointed()
    {
        var result = await new VfsExecTool(Registry(jailed: false)).RunAsync(Mount + _machine, "pwd");

        result.ShouldBeOk()["stdout"]!.GetValue<string>().Trim().ShouldBe(Physical(_machine));
    }

    // Generated from the same flag that decides the behaviour, so the prose cannot claim a shell
    // the machine does not offer.
    [Fact]
    public void TheGeneratedDescription_SaysWhetherCommandsCanBeRun()
    {
        Executing(jailed: false).DescribeMount.ShouldContain("Commands can be run");
        Plain().DescribeMount.ShouldContain("Commands cannot be run");
    }

    // The whole reason a mount declares its own landing target: an exec-capable outpost would
    // otherwise have started receiving a person's attachments onto their own machine.
    [Fact]
    public async Task AnExecCapableOutpostBesideASandbox_StillSendsAttachmentsToTheSandbox()
    {
        var sandbox = new LandingSandbox();
        var registry = new VirtualFileSystemRegistry();
        registry.Mount(
            new FileSystemMount("sandbox", "/sandbox", "a sandbox")
            {
                Capabilities = [VfsExecTool.Name], Workspace = "home/sandbox_user", IsLandingTarget = true
            },
            sandbox);
        var outpost = Executing(jailed: false);
        registry.Mount(
            new FileSystemMount("laptop", Mount, outpost.DescribeMount)
            {
                Capabilities = [VfsExecTool.Name],
                Workspace = outpost.Workspace,
                IsLandingTarget = outpost.IsLandingTarget
            },
            outpost);

        var outcome = await AttachmentLanding.LandAsync(
            new AttachmentLanding.Landing(
                registry,
                [new AttachmentReference { Id = "7-42/a", FileName = "photo.png", MediaType = "image/png", SizeBytes = 4 }],
                (_, _) => Task.FromResult<byte[]?>([1, 2, 3, 4]),
                "7:42",
                "m1"),
            NullLogger.Instance,
            CancellationToken.None);

        outcome.Landed.ShouldHaveSingleItem().ShouldStartWith("/sandbox/");
        sandbox.Writes.ShouldHaveSingleItem();
        Directory.EnumerateFileSystemEntries(_workingDirectory).ShouldBeEmpty();
    }

    private static IReadOnlyList<string> RegisteredFileSystemTools(bool exec)
    {
        var services = new ServiceCollection();
        services.ConfigureMcp(new OutpostSettings
        {
            Name = "laptop",
            WorkingDirectory = "/home/someone/project",
            Exec = exec
        });
        using var provider = services.BuildServiceProvider();

        return
        [
            .. provider.GetServices<McpServerTool>()
                .Select(tool => tool.ProtocolTool.Name)
                .Where(FileSystemOperations.ToolNames.Contains)
        ];
    }

    // The temp directory can sit behind a symlink (/tmp on a mac, /var/folders), and `pwd` reports
    // where the shell really is.
    private static string Physical(string path) => new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName ?? path;

    private OutpostFileSystem Plain() =>
        new("laptop", new LocalFileSystemClient(), _workingDirectory, [".md"]);

    private ExecutingOutpostFileSystem Executing(bool jailed) =>
        new("laptop", new LocalFileSystemClient(), _workingDirectory, [".md"], jailed,
            new BashRunner(new BashRunnerOptions
            {
                ContainerRoot = OutpostFileSystem.MountRoot,
                DefaultTimeoutSeconds = 10,
                MaxTimeoutSeconds = 30,
                OutputCapBytes = 65536
            }));

    private VirtualFileSystemRegistry Registry(bool jailed)
    {
        var registry = new VirtualFileSystemRegistry();
        var backend = Executing(jailed);
        registry.Mount(new FileSystemMount("laptop", Mount, backend.DescribeMount), backend);
        return registry;
    }

    private sealed class LandingSandbox : FileSystemBackendBase
    {
        public List<string> Writes { get; } = [];

        public override string FilesystemName => "sandbox";
        public override string DescribeMount => "a sandbox";
        public override bool IsLandingTarget => true;
        public override string Workspace => "home/sandbox_user";

        public override async Task<long> WriteChunksAsync(
            string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
            bool overwrite, bool createDirectories, CancellationToken ct)
        {
            long written = 0;
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                written += chunk.Length;
            }

            Writes.Add(path);
            return written;
        }

        public override Task<FsResult<FsExecResult>> ExecAsync(
            string path, string command, int? timeoutSeconds, CancellationToken ct) =>
            throw new NotSupportedException("nothing in this test runs a command in the sandbox");
    }
}