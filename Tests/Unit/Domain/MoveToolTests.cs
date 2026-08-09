using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

public class MoveToolTests
{
    private readonly Mock<IFileSystemClient> _clientMock = new();

    private readonly string _libraryPath = OperatingSystem.IsWindows()
        ? @"C:\media\library"
        : "/media/library";

    private TestableMoveToolWrapper CreateTool()
    {
        return new TestableMoveToolWrapper(
            _clientMock.Object,
            new LibraryPathConfig(_libraryPath));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Run_WithRelativePaths_ResolvesAgainstLibraryRoot(bool relativeSource, bool relativeDestination)
    {
        // Arrange
        var tool = CreateTool();
        var source = relativeSource
            ? Path.Combine("movies", "old.mkv")
            : Path.Combine(_libraryPath, "movies", "old.mkv");
        var destination = relativeDestination
            ? Path.Combine("movies", "new.mkv")
            : Path.Combine(_libraryPath, "movies", "new.mkv");
        var expectedSource = Path.Combine(_libraryPath, "movies", "old.mkv");
        var expectedDestination = Path.Combine(_libraryPath, "movies", "new.mkv");

        // Act
        var result = await tool.TestRun(source, destination, CancellationToken.None);

        // Assert
        result["status"]!.ToString().ShouldBe("success");
        result["source"]!.ToString().ShouldBe(expectedSource);
        result["destination"]!.ToString().ShouldBe(expectedDestination);
        _clientMock.Verify(
            m => m.Move(expectedSource, expectedDestination, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Run_WithDoubleDotInPath_ReturnsInvalidArgument(bool absolute, bool inSource)
    {
        // Arrange
        var tool = CreateTool();
        var maliciousPath = absolute
            ? Path.Combine(_libraryPath, "..", "etc", "passwd")
            : Path.Combine("..", "etc", "passwd");
        var validPath = Path.Combine(_libraryPath, "movies", "test.mkv");
        var source = inSource ? maliciousPath : validPath;
        var destination = inSource ? validPath : maliciousPath;

        // Act & Assert
        (await tool.TestRun(source, destination, CancellationToken.None))
            .ShouldBeError(ToolError.Codes.InvalidArgument);
    }

    // ".." as a substring of a name is not a traversal — the jail judges the resolved path.
    [Fact]
    public async Task Run_WithANameContainingConsecutiveDots_Succeeds()
    {
        var tool = CreateTool();
        var source = Path.Combine(_libraryPath, "movies", "v1..2.mkv");
        var destination = Path.Combine(_libraryPath, "movies", "v1..3.mkv");

        var result = await tool.TestRun(source, destination, CancellationToken.None);

        result["status"]!.ToString().ShouldBe("success");
        _clientMock.Verify(m => m.Move(source, destination, It.IsAny<CancellationToken>()), Times.Once);
    }

    private class TestableMoveToolWrapper(
        IFileSystemClient client,
        LibraryPathConfig libraryPath)
        : MoveTool(client, libraryPath)
    {
        public async Task<JsonNode> TestRun(string sourcePath, string destinationPath, CancellationToken ct)
            => (await Run(sourcePath, destinationPath, ct)).ToNode();
    }
}