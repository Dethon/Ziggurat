using Domain.Contracts;
using Domain.DTOs;
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

        runner.Verify(r => r.RunAsync("", "ls /vault", null, grant, It.IsAny<CancellationToken>()), Times.Once);
        runner.Verify(r => r.RunAsync("", "ls", null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Every write of the server's file tools names its path to the runner, which hands it to the
    // command's user before the next command (the launcher's adoption) — the destination for a
    // copy or a move, since that is what the server created.
    [Fact]
    public async Task EveryWrite_NamesWhatItWroteToTheRunner()
    {
        var root = Directory.CreateTempSubdirectory("sandbox-writes-").FullName;
        try
        {
            var home = Path.Combine(root, "home");
            Directory.CreateDirectory(home);
            var wrote = new List<string>();
            var runner = new Mock<ICommandRunner>();
            runner.Setup(r => r.Wrote(It.IsAny<string>())).Callback<string>(wrote.Add);
            var sandbox = Sandbox(root, home, runner.Object);

            await sandbox.CreateAsync("home/a/x.py", "print(1)", false, true, CancellationToken.None);
            await sandbox.EditAsync("home/a/x.py", [new TextEdit("1", "2")], CancellationToken.None);
            await sandbox.CopyAsync("home/a/x.py", "home/b/y.py", false, true, CancellationToken.None);
            await sandbox.MoveAsync("home/b/y.py", "home/c/z.py", CancellationToken.None);
            await sandbox.WriteBlobAsync("home/d/blob.bin", Convert.ToBase64String([1, 2]), 0, false, true, CancellationToken.None);
            await sandbox.WriteChunksAsync("home/e/chunks.bin", Chunks(), false, true, CancellationToken.None);

            wrote.ShouldBe([
                Path.Combine(home, "a", "x.py"),
                Path.Combine(home, "a", "x.py"),
                Path.Combine(home, "b", "y.py"),
                Path.Combine(home, "c", "z.py"),
                Path.Combine(home, "d", "blob.bin"),
                Path.Combine(home, "e", "chunks.bin")
            ]);
        }
        finally
        {
            Directory.Delete(root, true);
        }

        static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks()
        {
            await Task.Yield();
            yield return new byte[] { 1, 2, 3 };
        }
    }

    private static SandboxFileSystem Sandbox(string containerRoot, string homeDirectory, ICommandRunner? runner = null) =>
        new("sandbox", "a sandbox", Mock.Of<IFileSystemClient>(),
            new LibraryPathConfig(containerRoot), [".py"], runner ?? Mock.Of<ICommandRunner>(), homeDirectory);
}