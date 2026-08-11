using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsTextEditToolTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static (Mock<IVirtualFileSystemRegistry> Registry, List<TextEdit>? Captured) WireCapturing(out Func<List<TextEdit>?> read)
    {
        List<TextEdit>? captured = null;
        var backend = new Mock<IFileSystemBackend>();
        backend
            .Setup(b => b.EditAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<TextEdit>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<TextEdit>, CancellationToken>((_, edits, _) => captured = edits.ToList())
            .ReturnsAsync(new FsResult<FsEditResult>.Ok(new FsEditResult
            {
                Status = "edited", FilePath = "/vault/a.md", TotalOccurrencesReplaced = 1,
                Edits = [new FsEditDetail { OccurrencesReplaced = 1, AffectedLines = new FsLineRange { Start = 1, End = 1 } }]
            }));

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns<string>(path => Resolved(backend.Object, path));
        registry.Setup(r => r.GetMounts())
            .Returns([new FileSystemMount("vault", "/vault", "Vault")]);

        read = () => captured;
        return (registry, captured);
    }

    [Fact]
    public async Task Factory_ObjectNewString_Binds_UsesJsonText_AndNotes()
    {
        var (registry, _) = WireCapturing(out var read);
        var edit = new FileSystemToolFeature(registry.Object)
            .GetTools(new FeatureConfig())
            .Single(t => t.Name == "domain__filesystem__text_edit");

        var args = new AIFunctionArguments
        {
            ["filePath"] = Json("\"/vault/a.md\""),
            ["edits"] = Json("[{\"oldString\":\"a\",\"newString\":{\"k\":1}}]")
        };
        var result = await edit.InvokeAsync(args);

        var captured = read();
        captured.ShouldNotBeNull();
        captured!.Single().NewString.ShouldBe("{\"k\":1}");
        JsonSerializer.Serialize(result).ShouldContain("note");
    }

    [Fact]
    public async Task Factory_AllStringEdits_NoNote()
    {
        var (registry, _) = WireCapturing(out _);
        var edit = new FileSystemToolFeature(registry.Object)
            .GetTools(new FeatureConfig())
            .Single(t => t.Name == "domain__filesystem__text_edit");

        var args = new AIFunctionArguments
        {
            ["filePath"] = Json("\"/vault/a.md\""),
            ["edits"] = Json("[{\"oldString\":\"a\",\"newString\":\"b\"}]")
        };
        var result = await edit.InvokeAsync(args);

        JsonSerializer.Serialize(result).ShouldNotContain("note");
    }

    [Fact]
    public void Factory_Schema_KeepsEditStringsString_AndHidesInjectedParams()
    {
        var (registry, _) = WireCapturing(out _);
        var edit = new FileSystemToolFeature(registry.Object)
            .GetTools(new FeatureConfig())
            .Single(t => t.Name == "domain__filesystem__text_edit");

        var properties = edit.JsonSchema.GetProperty("properties");
        properties.TryGetProperty("arguments", out _).ShouldBeFalse();
        properties.TryGetProperty("cancellationToken", out _).ShouldBeFalse();

        var itemProps = properties.GetProperty("edits").GetProperty("items").GetProperty("properties");
        SchemaTypeAllows(itemProps.GetProperty("oldString"), "string").ShouldBeTrue();
        SchemaTypeAllows(itemProps.GetProperty("newString"), "string").ShouldBeTrue();
    }

    private static bool SchemaTypeAllows(JsonElement schema, string type)
    {
        var typeNode = schema.GetProperty("type");
        return typeNode.ValueKind == JsonValueKind.String
            ? typeNode.GetString() == type
            : typeNode.EnumerateArray().Any(t => t.GetString() == type);
    }

    private static FsResult<FileSystemResolution> Resolved(
        IFileSystemBackend backend, string relativePath, string mountPoint = "") =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath, mountPoint));

}