using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsQueryToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();

    [Fact]
    public async Task RunAsync_AsksTheMountAndAnswersAtTheCallersPath()
    {
        _registry.Setup(r => r.Resolve("/sugary/meals"))
            .Returns(new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(_backend.Object, "meals", "/sugary")));
        _backend.Setup(b => b.QueryAsync("meals", "select 1 as one", VfsQueryTool.DefaultMaxRows, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsQueryResult>.Ok(new FsQueryResult
            {
                Path = "meals",
                Columns = [new FsQueryColumn("one", "INTEGER")],
                Rows = [[JsonValue.Create(1)]],
                RowCount = 1,
                Truncated = false
            }));

        var result = await new VfsQueryTool(_registry.Object).RunAsync("/sugary/meals", "select 1 as one");

        result["path"]!.GetValue<string>().ShouldBe("/sugary/meals");
        result["columns"]![0]!["name"]!.GetValue<string>().ShouldBe("one");
        result["rows"]![0]![0]!.GetValue<int>().ShouldBe(1);
        result["rowCount"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_HoldsTheRowCapToItsCeiling()
    {
        _registry.Setup(r => r.Resolve("/sugary"))
            .Returns(new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(_backend.Object, "", "/sugary")));
        _backend.Setup(b => b.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsQueryResult>.Ok(new FsQueryResult
            {
                Path = "", Columns = [], Rows = [], RowCount = 0, Truncated = false
            }));

        await new VfsQueryTool(_registry.Object).RunAsync("/sugary", "select 1", maxRows: 1_000_000);

        _backend.Verify(b => b.QueryAsync("", "select 1", VfsQueryTool.MaxRowsCeiling, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task RunAsync_AnUnmountedPath_IsTheRegistrysEnvelope()
    {
        _registry.Setup(r => r.Resolve("/nowhere"))
            .Returns(FsError.NotFound<FileSystemResolution>("/nowhere"));

        var result = await new VfsQueryTool(_registry.Object).RunAsync("/nowhere", "select 1");

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
    }
}