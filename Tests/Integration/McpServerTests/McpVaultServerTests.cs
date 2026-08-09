using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

public class McpVaultServerTests(McpVaultServerFixture fixture) : IClassFixture<McpVaultServerFixture>
{
    [Fact]
    public async Task McpServer_ListResources_ReturnsFilesystemResource()
    {
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var resources = await client.ListResourcesAsync();

        resources.ShouldNotBeEmpty();
        var fsResource = resources.FirstOrDefault(r => r.Uri.StartsWith("filesystem://"));
        fsResource.ShouldNotBeNull();
        fsResource.Uri.ShouldBe("filesystem://vault");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task McpServer_ReadFilesystemResource_ReturnsMetadata()
    {
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var content = await client.ReadResourceAsync("filesystem://vault");
        var text = string.Join("", content.Contents
            .OfType<TextResourceContents>()
            .Select(c => c.Text));

        text.ShouldContain("\"name\":\"vault\"");
        text.ShouldContain("\"mountPoint\":\"/vault\"");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task McpServer_ListTools_ReturnsAllFsTools()
    {
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var tools = await client.ListToolsAsync();
        var toolNames = tools.Select(t => t.Name).ToList();

        toolNames.ShouldContain("fs_read");
        toolNames.ShouldContain("fs_create");
        toolNames.ShouldContain("fs_edit");
        toolNames.ShouldContain("fs_glob");
        toolNames.ShouldContain("fs_search");
        toolNames.ShouldContain("fs_move");
        toolNames.ShouldContain("fs_delete");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task FsReadTool_WithValidFile_ReturnsContent()
    {
        fixture.CreateFile("test-read.md", "# Hello World");

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var result = await client.CallToolAsync(
            "fs_read",
            new Dictionary<string, object?>
            {
                ["path"] = "test-read.md"
            },
            cancellationToken: CancellationToken.None);

        result.ShouldNotBeNull();
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        text.ShouldContain("Hello World");

        await client.DisposeAsync();
    }
}