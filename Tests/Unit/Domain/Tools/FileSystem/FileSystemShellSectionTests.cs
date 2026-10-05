using Domain.Contracts;
using Domain.DTOs;
using Domain.Outposts;
using Domain.Tools.FileSystem;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

// What the model is told about the shell reaching the other mounts: which ones, at which path —
// including a mount the image left room for only under /vfs — which execs run in the sandbox, and
// that the result's vfsChanges is the record. Said only where it is true: a session with a sandbox
// and a bridge to serve the mounts through.
public class FileSystemShellSectionTests
{
    private static readonly IReadOnlyList<FileSystemMount> _mounts =
    [
        new("vault", "/vault", "Notes."),
        new("media", "/media", "Library."),
        new("timers", "/timers", "Timers.") { Capabilities = ["file_read", "exec"] },
        new("sandbox", "/sandbox", "Sandbox.") { ShellReach = ShellReach.Contained, OccupiedNames = ["etc", "media", "sandbox"] },
        new("laptop", OutpostMountPoint.For("laptop"), "A machine.")
    ];

    [Fact]
    public void TheSection_NamesEachServedMountAtItsPath_AndACollidedOneUnderVfs()
    {
        var section = FileSystemToolFeature.ShellSection(_mounts);

        section.ShouldContain("`/vault`");
        section.ShouldContain("`/media` only at `/vfs/media`");
        section.ShouldNotContain("laptop");
        section.ShouldContain("`exec` on `/timers` runs in the sandbox");
        section.ShouldContain("vfsChanges");
    }

    [Fact]
    public void ASessionWithNoSandbox_IsToldNothingAboutIt()
    {
        FileSystemToolFeature.ShellSection([.. _mounts.Where(m => m.ShellReach is null)]).ShouldBeEmpty();
    }

    [Fact]
    public void ThePromptAndTheExecTool_SayItOnlyWhereABridgeServesTheMounts()
    {
        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.GetMounts()).Returns(_mounts);
        var bridged = new FileSystemToolFeature(registry.Object, bridge: new VfsBridge(new FakeTimeProvider(), new VfsBridgeSettings()));
        var plain = new FileSystemToolFeature(registry.Object);
        var execOnly = new FeatureConfig(EnabledTools: new HashSet<string>([VfsExecTool.Key]));

        bridged.Prompt!.ShouldContain("### The other mounts inside a command");
        plain.Prompt!.ShouldNotContain("### The other mounts inside a command");
        bridged.GetTools(execOnly).Single().Description.ShouldContain("vfsChanges");
        plain.GetTools(execOnly).Single().Description.ShouldNotContain("vfsChanges");
    }
}