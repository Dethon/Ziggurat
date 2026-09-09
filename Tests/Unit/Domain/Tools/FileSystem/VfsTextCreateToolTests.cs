using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsTextCreateToolTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static (Mock<IVirtualFileSystemRegistry> Registry, Mock<IFileSystemBackend> Backend) Wire(
        Action<string>? captureContent = null)
    {
        var backend = new Mock<IFileSystemBackend>();
        backend
            .Setup(b => b.CreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, bool, CancellationToken>((_, content, _, _, _) => captureContent?.Invoke(content))
            .ReturnsAsync(new FsResult<FsCreateResult>.Ok(new FsCreateResult
            {
                Status = "created", FilePath = "/schedules/x/schedule.json", Size = "34 B", Lines = 1
            }));

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns<string>(path => Resolved(backend.Object, path));
        registry.Setup(r => r.GetMounts())
            .Returns([new FileSystemMount("schedules", "/schedules", "Scheduled tasks")]);
        return (registry, backend);
    }

    // A backend that accepted a body at the schedule's directory wrote schedule.json under it.
    // Echoing the caller's own path back said the file was where the caller aimed, and the model
    // moved it "into place" — a move the mount refused, and a remove after that. The backend's
    // answer wins when it extends the path the caller gave.
    [Fact]
    public async Task ABackendThatWroteUnderTheCallersPath_NamesTheFileItWrote()
    {
        var backend = new Mock<IFileSystemBackend>();
        backend
            .Setup(b => b.CreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsCreateResult>.Ok(new FsCreateResult
            {
                Status = "created", FilePath = "/nabu/apagar-aire/schedule.json", Size = "34 B", Lines = 1
            }));
        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/schedules/nabu/apagar-aire"))
            .Returns(Resolved(backend.Object, "nabu/apagar-aire", "/schedules"));

        var node = await new VfsTextCreateTool(registry.Object).RunAsync("/schedules/nabu/apagar-aire", "{}");

        node["filePath"]!.GetValue<string>().ShouldBe("/schedules/nabu/apagar-aire/schedule.json");
    }

    [Fact]
    public async Task Body_NoNote_WhenContentArgWasString()
    {
        var (registry, _) = Wire();
        var tool = new VfsTextCreateTool(registry.Object);
        var args = new AIFunctionArguments { ["content"] = Json("\"hello\"") };

        var node = await tool.RunAsync("/vault/a.md", "hello", arguments: args);

        node.ToJsonString().ShouldNotContain("note");
    }

    [Fact]
    public async Task Factory_ObjectContent_Binds_WritesJsonText_AndNotes()
    {
        string? captured = null;
        var (registry, _) = Wire(c => captured = c);
        var create = new FileSystemToolFeature(registry.Object)
            .GetTools(new FeatureConfig())
            .Single(t => t.Name == "domain__filesystem__text_create");

        var args = new AIFunctionArguments
        {
            ["filePath"] = Json("\"/schedules/x/schedule.json\""),
            ["content"] = Json("{\"cron\":\"0 9 * * *\",\"prompt\":\"hi\"}")
        };
        var result = await create.InvokeAsync(args);

        captured.ShouldBe("{\"cron\":\"0 9 * * *\",\"prompt\":\"hi\"}");
        JsonSerializer.Serialize(result).ShouldContain("note");
    }

    [Fact]
    public void Factory_Schema_KeepsContentString_AndHidesInjectedParams()
    {
        var (registry, _) = Wire();
        var create = new FileSystemToolFeature(registry.Object)
            .GetTools(new FeatureConfig())
            .Single(t => t.Name == "domain__filesystem__text_create");

        var properties = create.JsonSchema.GetProperty("properties");
        properties.GetProperty("content").GetProperty("type").GetString().ShouldBe("string");
        properties.TryGetProperty("arguments", out _).ShouldBeFalse();
        properties.TryGetProperty("cancellationToken", out _).ShouldBeFalse();
    }

    private static FsResult<FileSystemResolution> Resolved(
        IFileSystemBackend backend, string relativePath, string mountPoint = "") =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath, mountPoint));

}