using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class FileSystemToolFeatureTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly FileSystemToolFeature _feature;

    public FileSystemToolFeatureTests()
    {
        _registry.Setup(r => r.GetMounts()).Returns([
            new FileSystemMount("library", "/library", "Personal document library")
        ]);
        _feature = new FileSystemToolFeature(_registry.Object);
    }

    [Fact]
    public void FeatureName_IsFilesystem()
    {
        _feature.FeatureName.ShouldBe("filesystem");
    }

    // GetTools is a hand-written list sitting beside the one operation table, and nothing bound the
    // two together: an eleventh operation could join FileSystemOperations.All, be enabled by config
    // and produce no tool — half-existing, which is the thing that list exists to prevent.
    // FileSystemOperationsTests binds the two together; here each declared key is driven.
    public static TheoryData<string> DeclaredToolKeys() => new(FileSystemToolFeature.AllToolKeys);

    [Theory]
    [MemberData(nameof(DeclaredToolKeys))]
    public void GetTools_EachDeclaredKey_EnablesExactlyOneTool(string key)
    {
        var config = new FeatureConfig(EnabledTools: new HashSet<string>([key], StringComparer.OrdinalIgnoreCase));

        _feature.GetTools(config).Count().ShouldBe(1);
    }

    [Fact]
    public void GetTools_FilteredEnabledTools_ReturnsOnlyMatching()
    {
        var config = new FeatureConfig(
            EnabledTools: new HashSet<string>(["read", "move"], StringComparer.OrdinalIgnoreCase));
        var tools = _feature.GetTools(config).ToList();

        tools.Count.ShouldBe(2);
        tools.Select(t => t.Name).ShouldContain("domain__filesystem__text_read");
        tools.Select(t => t.Name).ShouldContain("domain__filesystem__move");
    }

    [Fact]
    public void GetTools_EmptyEnabledTools_ReturnsNoTools()
    {
        var config = new FeatureConfig(
            EnabledTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var tools = _feature.GetTools(config).ToList();

        tools.ShouldBeEmpty();
    }

    [Fact]
    public void Prompt_ContainsMountPoints()
    {
        _feature.Prompt.ShouldNotBeNull();
        _feature.Prompt.ShouldContain("/library");
        _feature.Prompt.ShouldContain("Personal document library");
    }

    [Fact]
    public void Prompt_ReturnsNull_WhenNoMounts()
    {
        var emptyRegistry = new Mock<IVirtualFileSystemRegistry>();
        emptyRegistry.Setup(r => r.GetMounts()).Returns([]);
        var feature = new FileSystemToolFeature(emptyRegistry.Object);

        feature.Prompt.ShouldBeNull();
    }

    [Fact]
    public void Prompt_ListsPerMountSupportedOperations()
    {
        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.GetMounts()).Returns([
            new FileSystemMount("ha", "/ha", "Home Assistant")
            {
                Capabilities = ["text_read", "glob", "text_search", "file_info", "exec"]
            }
        ]);
        var feature = new FileSystemToolFeature(registry.Object);

        feature.Prompt.ShouldNotBeNull();
        feature.Prompt.ShouldContain("operations: text_read, glob, text_search, file_info, exec");
    }

    // This is the one prompt built per agent from its actual mount set, so its fixed text must
    // hold for any of them. Mount-choice guidance arrived here from mcp-vault as a hard-coded
    // vault-vs-sandbox table; stated that way it is wrong for an agent mounting neither, and the
    // mistake reads as normal prose in review. The mounts here are named nothing of the sort, so
    // either name appearing in the output can only have come from the fixed text.
    [Fact]
    public void Prompt_NamesNoDeploymentSpecificMount()
    {
        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.GetMounts()).Returns([
            new FileSystemMount("notes", "/notes", "Notes") { Capabilities = ["text_read", "text_edit"] },
            new FileSystemMount("box", "/box", "Box") { Capabilities = ["text_read", "exec"] }
        ]);
        var feature = new FileSystemToolFeature(registry.Object);

        feature.Prompt.ShouldNotBeNull();
        feature.Prompt.ShouldNotContain("vault");
        feature.Prompt.ShouldNotContain("sandbox");
    }

    [Fact]
    public void Prompt_ReadOnlyStyleMount_DoesNotAdvertiseWriteOrExec()
    {
        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.GetMounts()).Returns([
            new FileSystemMount("media", "/media", "Library")
            {
                Capabilities = ["text_read", "glob", "text_search", "move", "copy", "remove", "file_info"]
            }
        ]);
        var feature = new FileSystemToolFeature(registry.Object);

        var operationsLine = feature.Prompt!.Split('\n').Single(l => l.Contains("operations:"));
        operationsLine.ShouldNotContain("text_create");
        operationsLine.ShouldNotContain("exec");
        operationsLine.ShouldContain("text_read");
    }

    [Fact]
    public void Prompt_MountWithoutCapabilities_OmitsOperationsLine()
    {
        // The default _registry mount carries no capabilities.
        _feature.Prompt!.ShouldNotContain("operations:");
    }

    // The prompt promises the model that errors arrive as data, and warns it specifically about
    // bare paths. Resolution used to throw, so the mistake the prompt warns about broke the promise
    // at every tool site at once. Every tool the feature produces is checked here.
    public static TheoryData<string, Dictionary<string, object?>> UnmountedPathCalls() => new()
    {
        { "text_read", new() { ["filePath"] = "/notes/x.md" } },
        { "text_create", new() { ["filePath"] = "/notes/x.md", ["content"] = "hi" } },
        {
            "text_edit",
            new()
            {
                ["filePath"] = "/notes/x.md",
                ["edits"] = JsonNode.Parse("""[{"oldString":"a","newString":"b"}]""")
            }
        },
        { "glob", new() { ["basePath"] = "/notes", ["pattern"] = "*" } },
        { "text_search", new() { ["query"] = "q", ["filePath"] = "/notes/x.md" } },
        { "move", new() { ["sourcePath"] = "/notes/x.md", ["destinationPath"] = "/notes/y.md" } },
        { "copy", new() { ["sourcePath"] = "/notes/x.md", ["destinationPath"] = "/notes/y.md" } },
        { "remove", new() { ["path"] = "/notes/x.md" } },
        { "exec", new() { ["path"] = "/notes", ["command"] = "ls" } },
        { "file_info", new() { ["path"] = "/notes/x.md" } }
    };

    [Theory]
    [MemberData(nameof(UnmountedPathCalls))]
    public async Task GetTools_UnmountedPath_ReturnsAnErrorEnvelopeRatherThanThrowing(
        string toolName, Dictionary<string, object?> arguments)
    {
        var registry = new VirtualFileSystemRegistry();
        registry.Mount(new FileSystemMount("library", "/library", "Library"), Mock.Of<IFileSystemBackend>());
        var tool = new FileSystemToolFeature(registry)
            .GetTools(new FeatureConfig())
            .Single(t => t.Name == $"domain__filesystem__{toolName}");

        var result = await tool.InvokeAsync(new AIFunctionArguments(arguments));

        var json = JsonSerializer.Serialize(result);
        json.ShouldContain("\"ok\":false");
        json.ShouldContain("/notes");
        json.ShouldContain("/library");
    }

    // A destination on no mount is as likely a mistake as a source, and the transfer tools resolve
    // both, so neither resolution may throw.
    [Theory]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task GetTools_UnmountedDestination_ReturnsAnErrorEnvelope(string toolName)
    {
        var registry = new VirtualFileSystemRegistry();
        registry.Mount(new FileSystemMount("library", "/library", "Library"), Mock.Of<IFileSystemBackend>());
        var tool = new FileSystemToolFeature(registry)
            .GetTools(new FeatureConfig())
            .Single(t => t.Name == $"domain__filesystem__{toolName}");

        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["sourcePath"] = "/library/x.md",
            ["destinationPath"] = "/notes/y.md"
        }));

        JsonSerializer.Serialize(result).ShouldContain("\"ok\":false");
    }
}