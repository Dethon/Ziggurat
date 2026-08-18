using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Infrastructure.Agents;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class VirtualFileSystemRegistryTests
{
    private readonly VirtualFileSystemRegistry _registry = new();

    [Fact]
    public void Mount_RegistersMount()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Personal document library"), backend);

        var mounts = _registry.GetMounts();
        mounts.Count.ShouldBe(1);
        mounts[0].Name.ShouldBe("library");
        mounts[0].MountPoint.ShouldBe("/library");
    }

    // A mount point is a name the model addresses, so two mounts claiming one is a collision
    // somebody has to lose. The one already there wins: it was configured, and the challenger is a
    // machine that named itself. Outposts are mounted after the configured filesystems for exactly
    // this reason, so which one loses is decided by mount order rather than by timing.
    [Fact]
    public void Mount_AMountPointAlreadyTaken_IsShadowedAndTheExistingMountIsUntouched()
    {
        var vault = CreateMockBackend("vault");
        _registry.Mount(new FileSystemMount("vault", "/vault", "The real vault"), vault);

        _registry.TryMount(
                new FileSystemMount("vault", "/vault", "Somebody's laptop calling itself the vault"),
                CreateMockBackend("impostor"))
            .ShouldBeFalse();

        var mounts = _registry.GetMounts();
        mounts.Count.ShouldBe(1);
        mounts[0].Description.ShouldBe("The real vault");
        Resolve("/vault/notes.md").Backend.ShouldBe(vault);
    }

    [Fact]
    public void Mount_AFreeMountPoint_IsTaken()
    {
        _registry.TryMount(new FileSystemMount("laptop", "/laptop", "A laptop"), CreateMockBackend("laptop"))
            .ShouldBeTrue();

        _registry.GetMounts().Select(m => m.Name).ShouldBe(["laptop"]);
    }

    [Fact]
    public void Resolve_MatchingMount_ReturnsBackendAndRelativePath()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        var resolution = Resolve("/library/notes/todo.md");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("notes/todo.md");
    }

    [Fact]
    public void Resolve_RootPath_ReturnsEmptyRelativePath()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        var resolution = Resolve("/library");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("");
    }

    [Fact]
    public void Resolve_LongestPrefixWins()
    {
        var libraryBackend = CreateMockBackend("library");
        var docsBackend = CreateMockBackend("docs");

        _registry.Mount(new FileSystemMount("library", "/library", "Library"), libraryBackend);
        _registry.Mount(new FileSystemMount("docs", "/library/docs", "Docs"), docsBackend);

        var resolution = Resolve("/library/docs/readme.md");
        resolution.Backend.ShouldBe(docsBackend);
        resolution.RelativePath.ShouldBe("readme.md");
    }

    // Resolution answers with data, never an exception: the tool sites hand the envelope straight
    // to the model, which is what makes the "errors are data" promise in the prompt true.
    [Fact]
    public void Resolve_NoMatchingMount_ReturnsAnErrorNamingThePathAndTheMounts()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        _registry.Resolve("/unknown/file.md").TryGetValue(out _, out var error).ShouldBeFalse();

        // A name nothing here has ever had: absent, so nothing to wait for, and the recovery is
        // the list of mounts that do exist.
        error!.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
        error.Retryable.ShouldBeFalse();
        error.Message.ShouldContain("No filesystem mounted");
        error.Message.ShouldContain("/unknown/file.md");
        error.Recovery!.ShouldContain("/library");
    }

    // The same miss, for a machine that registered and then did not answer when this conversation
    // started. It is the one absence worth trying again, and reading it as a typo — which is what
    // every miss used to say — sends the model looking for a path that was spelled right.
    [Fact]
    public void Resolve_AMountThisSessionKnowsIsUnreachable_SaysSoAndInvitesARetry()
    {
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), CreateMockBackend("library"));
        _registry.DeclareAbsence("/laptop", CapabilityState.Unavailable, "it did not answer when this conversation started");

        _registry.Resolve("/laptop/notes.md").TryGetValue(out _, out var error).ShouldBeFalse();

        error!.ErrorCode.ShouldBe(ToolError.Codes.TransientDependency);
        error.Retryable.ShouldBeTrue();
        error.Message.ShouldContain("did not answer");
    }

    // The plain Mount obeys the same rule as TryMount above — this used to be last-write-wins, and
    // outposts are why it is not: a machine that named itself after an existing mount would
    // otherwise replace it, and a stranger's laptop could shadow the vault.
    [Fact]
    public void Mount_DuplicateMountPoint_LeavesTheFirstOneInPlace()
    {
        var backend1 = CreateMockBackend("lib1");
        var backend2 = CreateMockBackend("lib2");

        _registry.Mount(new FileSystemMount("lib1", "/library", "First"), backend1);
        _registry.Mount(new FileSystemMount("lib2", "/library", "Second"), backend2);

        Resolve("/library/file.md").Backend.ShouldBe(backend1);
    }

    [Fact]
    public void Resolve_CaseInsensitiveMatch()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        var resolution = Resolve("/Library/Notes/Todo.md");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("Notes/Todo.md");
    }

    [Fact]
    public void Resolve_PartialSegmentMatch_DoesNotMatch()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        _registry.Resolve("/libraryextra/file.md").TryGetValue(out _, out var error).ShouldBeFalse();

        error!.Message.ShouldContain("No filesystem mounted");
    }

    private FileSystemResolution Resolve(string virtualPath)
    {
        _registry.Resolve(virtualPath).TryGetValue(out var resolution, out var error)
            .ShouldBeTrue(error?.Message);
        return resolution!;
    }

    private static IFileSystemBackend CreateMockBackend(string name)
    {
        var mock = new Mock<IFileSystemBackend>();
        mock.Setup(b => b.FilesystemName).Returns(name);
        return mock.Object;
    }
}