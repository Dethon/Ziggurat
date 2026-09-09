using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsGlobFilesToolTests
{
    private static VfsGlobFilesTool Build(string mountPoint, string relativePath, FsGlobResult backendResult,
        out Mock<IFileSystemBackend> backend)
    {
        backend = new Mock<IFileSystemBackend>();
        backend.Setup(b => b.GlobAsync(relativePath, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(backendResult));

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns(Resolved(backend.Object, relativePath, mountPoint));

        return new VfsGlobFilesTool(registry.Object);
    }

    private static IReadOnlyList<string> Entries(System.Text.Json.Nodes.JsonNode node) =>
        node["entries"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    [Fact]
    public async Task Run_PrependsMountPoint_ToMountRelativeEntries()
    {
        var tool = Build("/ha", "entities", new FsGlobResult
        {
            Entries = ["entities/light/kitchen/", "entities/light/kitchen/state.json"],
            Truncated = false,
            Total = 2
        }, out _);

        var result = await tool.RunAsync("/ha/entities", "**", CancellationToken.None);

        Entries(result).ShouldBe(["/ha/entities/light/kitchen/", "/ha/entities/light/kitchen/state.json"]);
    }

    [Fact]
    public async Task Run_NormalizesLeadingSlashEntries_WithoutDoubleSlash()
    {
        var tool = Build("/print-queue", "", new FsGlobResult
        {
            Entries = ["/note.txt", "/status.json"],
            Truncated = false,
            Total = 2
        }, out _);

        var result = await tool.RunAsync("/print-queue", "*", CancellationToken.None);

        Entries(result).ShouldBe(["/print-queue/note.txt", "/print-queue/status.json"]);
    }

    // The walk's budget used to be explained in the tool's description, on every request of every
    // conversation, for a case most walks never meet. The explanation now travels with the result
    // that needs it: a walk that stopped before the tree ended says so and says what to do.
    [Fact]
    public async Task Run_BudgetReached_SaysSoInTheResult_AndHowToNarrow()
    {
        var tool = Build("/sandbox", "", new FsGlobResult
        {
            Entries = [],
            Truncated = false,
            Total = 0,
            EntriesScanned = 50_000,
            BudgetReached = true
        }, out _);

        var result = await tool.RunAsync("/sandbox", "**/*.csv", CancellationToken.None);

        var hint = result["hint"]!.GetValue<string>();
        hint.ShouldContain("50,000");
        hint.ShouldContain("basePath");
    }

    [Fact]
    public async Task Run_WalkEnded_CarriesNoHint()
    {
        var tool = Build("/sandbox", "", new FsGlobResult
        {
            Entries = [],
            Truncated = false,
            Total = 0,
            EntriesScanned = 12,
            BudgetReached = false
        }, out _);

        var result = await tool.RunAsync("/sandbox", "**/*.csv", CancellationToken.None);

        result["hint"].ShouldBeNull();
    }

    private static FsResult<FileSystemResolution> Resolved(
        IFileSystemBackend backend, string relativePath, string mountPoint = "") =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath, mountPoint));

}