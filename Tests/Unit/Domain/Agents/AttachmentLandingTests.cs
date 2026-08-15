using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.Domain.Agents;

// Landing picks its mount by the landing-target claim, never by the exec capability and never by
// a name. Asking for exec was enough while the sandbox was the only mount that could run
// anything; an outpost is a filesystem on somebody's real machine and may well be exec-capable,
// so under the old rule the first one of those would have started receiving their attachments.
public class AttachmentLandingTests
{
    private static readonly AttachmentReference _photo = new()
    {
        Id = "7-42/abc",
        FileName = "photo.png",
        MediaType = "image/png",
        SizeBytes = 4
    };

    [Fact]
    public async Task ASandboxBesideAnExecCapableOutpost_LandsInTheSandbox()
    {
        var sandbox = new RecordingBackend("sandbox");
        var outpost = new RecordingBackend("laptop");
        var registry = new StubRegistry(
            (Mount("sandbox", ["exec"], "home/sandbox_user", landingTarget: true), sandbox),
            (Mount("laptop", ["exec"], "home/someone", landingTarget: false), outpost));

        var outcome = await LandAsync(registry);

        outcome.Landed.ShouldHaveSingleItem().ShouldStartWith("/sandbox/home/sandbox_user/uploads/");
        outcome.Failed.ShouldBeEmpty();
        sandbox.Writes.ShouldHaveSingleItem();
        outpost.Writes.ShouldBeEmpty();
    }

    // The mount order is the reverse of the test above, so the answer cannot be "the first mount
    // with a workspace" passing by luck.
    [Fact]
    public async Task AnExecCapableOutpostListedFirst_StillLosesToTheSandbox()
    {
        var sandbox = new RecordingBackend("sandbox");
        var outpost = new RecordingBackend("laptop");
        var registry = new StubRegistry(
            (Mount("laptop", ["exec"], "home/someone", landingTarget: false), outpost),
            (Mount("sandbox", ["exec"], "home/sandbox_user", landingTarget: true), sandbox));

        await LandAsync(registry);

        sandbox.Writes.ShouldHaveSingleItem();
        outpost.Writes.ShouldBeEmpty();
    }

    // The behaviour ADR 0025 already established for a mount with no workspace, now reached by the
    // other route: a mount that can run something but is nobody's landing target lands nothing,
    // and the model is told which files it cannot act on rather than left to assume they arrived.
    [Fact]
    public async Task OnlyAnExecCapableMountThatIsNoLandingTarget_LandsNothingAndNamesTheFile()
    {
        var outpost = new RecordingBackend("laptop");
        var registry = new StubRegistry(
            (Mount("laptop", ["exec"], "home/someone", landingTarget: false), outpost));

        var outcome = await LandAsync(registry);

        outcome.Landed.ShouldBeEmpty();
        outcome.Failed.ShouldBe(["photo.png"]);
        outpost.Writes.ShouldBeEmpty();
    }

    // A landing target that cannot run anything is still where a file goes: the two claims are
    // separate, which is the whole point of splitting them.
    [Fact]
    public async Task ALandingTargetWithNoExecCapability_StillReceivesTheFile()
    {
        var store = new RecordingBackend("store");
        var registry = new StubRegistry(
            (Mount("store", [], "workspace", landingTarget: true), store));

        var outcome = await LandAsync(registry);

        outcome.Landed.ShouldHaveSingleItem().ShouldStartWith("/store/workspace/uploads/");
        store.Writes.ShouldHaveSingleItem();
    }

    private static Task<AttachmentLanding.LandingOutcome> LandAsync(StubRegistry registry) =>
        AttachmentLanding.LandAsync(
            new AttachmentLanding.Landing(
                registry,
                [_photo],
                (_, _) => Task.FromResult<byte[]?>([1, 2, 3, 4]),
                "7:42",
                "m1"),
            NullLogger.Instance,
            CancellationToken.None);

    private static FileSystemMount Mount(
        string name, IReadOnlyList<string> capabilities, string? workspace, bool landingTarget) =>
        new(name, $"/{name}", $"the {name} mount")
        {
            Capabilities = capabilities,
            Workspace = workspace,
            IsLandingTarget = landingTarget
        };

    private sealed class StubRegistry(params (FileSystemMount Mount, IFileSystemBackend Backend)[] mounts)
        : IVirtualFileSystemRegistry
    {
        public void Mount(FileSystemMount mount, IFileSystemBackend backend) { }

        public FsResult<FileSystemResolution> Resolve(string virtualPath) =>
            mounts
                .Where(m => virtualPath.StartsWith(m.Mount.MountPoint, StringComparison.Ordinal))
                .Select(m => new FsResult<FileSystemResolution>.Ok(
                    new FileSystemResolution(
                        m.Backend, virtualPath[m.Mount.MountPoint.Length..], m.Mount.MountPoint)))
                .Cast<FsResult<FileSystemResolution>>()
                .FirstOrDefault() ?? FsError.NotFound<FileSystemResolution>(virtualPath);

        public IReadOnlyList<FileSystemMount> GetMounts() => [.. mounts.Select(m => m.Mount)];
    }

    private sealed class RecordingBackend(string name) : FileSystemBackendBase
    {
        public List<string> Writes { get; } = [];

        public override string FilesystemName => name;

        public override string DescribeMount => $"the {name} mount";

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
    }
}