using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
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

        error!.Message.ShouldContain("No filesystem mounted");
        error.Message.ShouldContain("/unknown/file.md");
        error.Message.ShouldContain("/library");
    }

    [Fact]
    public void Mount_DuplicateMountPoint_LastWriteWins()
    {
        var backend1 = CreateMockBackend("lib1");
        var backend2 = CreateMockBackend("lib2");

        _registry.Mount(new FileSystemMount("lib1", "/library", "First"), backend1);
        _registry.Mount(new FileSystemMount("lib2", "/library", "Second"), backend2);

        var resolution = Resolve("/library/file.md");
        resolution.Backend.ShouldBe(backend2);
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