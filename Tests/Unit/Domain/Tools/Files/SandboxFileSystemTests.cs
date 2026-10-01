using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

// The mount publishes its workspace in its own coordinates — relative to the configured root —
// and landing composes the target from it (ADR 0025). Trimming slashes off the absolute HomeDir
// is only right when the root is "/": under any other root the published workspace resolves to a
// directory outside the persistent volume, which createDirectories then quietly makes true.
public class SandboxFileSystemTests
{
    [Theory]
    [InlineData("/", "/home/sandbox_user", "home/sandbox_user")]
    [InlineData("/srv/jail", "/srv/jail/home/user", "home/user")]
    public void TheWorkspace_IsTheHomeDirectoryRelativeToTheConfiguredRoot(
        string containerRoot, string homeDirectory, string workspace)
    {
        Sandbox(containerRoot, homeDirectory).Workspace.ShouldBe(workspace);
    }

    // A home that does not sit under the root has no spelling in the root's coordinates, and a
    // home equal to the root would publish the mount root as the workspace — the silent fallback
    // ADR 0025 exists to remove. Both are configuration errors, and a server must refuse to start
    // rather than land every attachment somewhere it does not mean.
    [Theory]
    [InlineData("/srv/jail", "/home/sandbox_user")]
    [InlineData("/srv/jail", "/srv/jail")]
    [InlineData("/srv/jail", "/srv/jail/../elsewhere")]
    public void AHomeDirectoryNotUnderTheRoot_FailsAtConstruction(
        string containerRoot, string homeDirectory)
    {
        Should.Throw<ArgumentException>(() => Sandbox(containerRoot, homeDirectory));
    }

    // The call token rides the call's `_meta`, and the mount as that caller sees it hands it to
    // the runner — the launcher, which gives it to the call's daemon and to nothing the command can
    // read. A call that carried none runs with no bridge.
    [Fact]
    public async Task AnExecForACallerHoldingAToken_HandsTheTokenToTheRunner()
    {
        var runner = new Mock<ICommandRunner>();
        var grant = new VfsBridgeGrant("tok-1");
        var sandbox = Sandbox("/", "/home/sandbox_user", runner.Object);

        await sandbox.For(new FileSystemCaller(null, grant)).ExecAsync("", "ls /vault", null, CancellationToken.None);
        await sandbox.ExecAsync("", "ls", null, CancellationToken.None);

        runner.Verify(r => r.RunAsync("", "ls /vault", null, It.IsAny<CancellationToken>(), grant), Times.Once);
        runner.Verify(r => r.RunAsync("", "ls", null, It.IsAny<CancellationToken>(), null), Times.Once);
    }

    private static SandboxFileSystem Sandbox(string containerRoot, string homeDirectory, ICommandRunner? runner = null) =>
        new("sandbox", "a sandbox", Mock.Of<IFileSystemClient>(),
            new LibraryPathConfig(containerRoot), [".py"], runner ?? Mock.Of<ICommandRunner>(), homeDirectory);
}