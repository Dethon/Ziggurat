using Domain.DTOs;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Domain.Tools.FileSystem;

[Collection("MultiFileSystem")]
public class VfsCopyToolIntegrationTests(MultiFileSystemFixture fx)
{
    [Fact]
    public async Task RunAsync_CrossFsTextFile_CopiesAndPreservesSource()
    {
        fx.CreateLibraryFile("hello.md", "from library");
        await using var libClient = await Connect(fx.LibraryEndpoint);
        await using var notesClient = await Connect(fx.NotesEndpoint);
        var registry = BuildRegistry(libClient, notesClient);
        var tool = new VfsCopyTool(registry);

        var result = await tool.RunAsync("/library/hello.md", "/notes/hello.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        File.Exists(Path.Combine(fx.LibraryPath, "hello.md")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(fx.NotesPath, "hello.md")).ShouldBe("from library");
    }

    [Fact]
    public async Task RunAsync_CrossFsBinaryFile_RoundtripsAllBytes()
    {
        var bytes = Enumerable.Range(0, 600 * 1024).Select(i => (byte)(i % 256)).ToArray();
        File.WriteAllBytes(Path.Combine(fx.LibraryPath, "blob.bin"), bytes);
        await using var libClient = await Connect(fx.LibraryEndpoint);
        await using var notesClient = await Connect(fx.NotesEndpoint);
        var registry = BuildRegistry(libClient, notesClient);
        var tool = new VfsCopyTool(registry);

        var result = await tool.RunAsync("/library/blob.bin", "/notes/blob.bin");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        File.ReadAllBytes(Path.Combine(fx.NotesPath, "blob.bin")).ShouldBe(bytes);
    }

    private static VirtualFileSystemRegistry BuildRegistry(McpClient libClient, McpClient notesClient)
    {
        var registry = new VirtualFileSystemRegistry();
        registry.Mount(new FileSystemMount("library", "/library", "lib"), new McpFileSystemBackend(libClient, "library", advertisedOperations: null));
        registry.Mount(new FileSystemMount("notes", "/notes", "notes"), new McpFileSystemBackend(notesClient, "notes", advertisedOperations: null));
        return registry;
    }

    private static async Task<McpClient> Connect(string endpoint)
        => await McpClient.CreateAsync(McpTestSecret.Transport(endpoint), loggerFactory: NullLoggerFactory.Instance);
}