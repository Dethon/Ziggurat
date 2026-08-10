using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

public class RemoveToolTests
{
    private readonly Mock<IFileSystemClient> _fileSystemClientMock = new();

    private readonly string _libraryPath = OperatingSystem.IsWindows()
        ? @"C:\media\library"
        : "/media/library";

    private TestableRemoveTool CreateTool()
    {
        return new TestableRemoveTool(
            _fileSystemClientMock.Object,
            new LibraryPathConfig(_libraryPath));
    }

    [Fact]
    public async Task Run_WithValidPath_MovesToTrash()
    {
        // Arrange
        var tool = CreateTool();
        var filePath = Path.Combine(_libraryPath, "movies", "test.mkv");
        var trashPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LocalFileSystemClient.TrashFolderName,
            "test.mkv");

        _fileSystemClientMock
            .Setup(m => m.MoveToTrash(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(trashPath);

        // Act
        var result = await tool.TestRun(filePath, CancellationToken.None);

        // Assert
        result["status"]!.ToString().ShouldBe("success");
        result["message"]!.ToString().ShouldBe("Moved to trash");
        result["originalPath"]!.ToString().ShouldBe(filePath);
        // The trash folder sits outside every mount, so no virtual path for it exists and nothing
        // reads the field. Reporting it would hand the model a location it cannot act on.
        result["trashPath"]!.ToString().ShouldBe("");
        _fileSystemClientMock.Verify(m => m.MoveToTrash(filePath, It.IsAny<CancellationToken>()), Times.Once);
    }

    // A '..' segment is judged by where it lands, not by its spelling: both of these resolve
    // outside the library, and that is what the jail refuses them for.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_WithPathTraversingOutOfTheLibrary_ReturnsInvalidArgument(bool isAbsolute)
    {
        // Arrange
        var tool = CreateTool();
        var maliciousPath = isAbsolute
            ? Path.Combine(_libraryPath, "..", "etc", "passwd")
            : Path.Combine("..", "etc", "passwd");

        // Act & Assert
        (await tool.TestRun(maliciousPath, CancellationToken.None))
            .ShouldBeError(ToolError.Codes.InvalidArgument)["message"]!.GetValue<string>()
            .ShouldContain("must be within the library");
        _fileSystemClientMock.Verify(m => m.MoveToTrash(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // MoveTool screens by whole segment, because the jail judges the resolved path; RemoveTool kept
    // rejecting the substring, so a file that could be created, globbed and moved through the mount
    // could never be removed through it.
    [Fact]
    public async Task Run_WithADoubleDotInsideAName_RemovesIt()
    {
        var tool = CreateTool();
        var filePath = Path.Combine(_libraryPath, "movies", "v1..2.mkv");
        _fileSystemClientMock
            .Setup(m => m.MoveToTrash(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync("trash-path");

        var result = await tool.TestRun(filePath, CancellationToken.None);

        result["status"]!.ToString().ShouldBe("success");
        _fileSystemClientMock.Verify(m => m.MoveToTrash(filePath, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WithRelativePath_ResolvesAgainstLibraryRoot()
    {
        // Arrange
        var tool = CreateTool();
        var relativePath = Path.Combine("movies", "test.mkv");
        var expectedAbsolutePath = Path.Combine(_libraryPath, "movies", "test.mkv");
        const string trashPath = "trash-path";

        _fileSystemClientMock
            .Setup(m => m.MoveToTrash(expectedAbsolutePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(trashPath);

        // Act
        var result = await tool.TestRun(relativePath, CancellationToken.None);

        // Assert
        result["status"]!.ToString().ShouldBe("success");
        result["originalPath"]!.ToString().ShouldBe(expectedAbsolutePath);
        _fileSystemClientMock.Verify(
            m => m.MoveToTrash(expectedAbsolutePath, It.IsAny<CancellationToken>()), Times.Once);
    }

    private class TestableRemoveTool(
        IFileSystemClient client,
        LibraryPathConfig libraryPath)
        : RemoveTool(client, libraryPath)
    {
        public async Task<JsonNode> TestRun(string path, CancellationToken ct)
        {
            return (await Run(path, ct)).ToNode();
        }
    }
}