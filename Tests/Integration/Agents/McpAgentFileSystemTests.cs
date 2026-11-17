using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

// The filesystem feature's tools, called the way a model calls them: the fake model asks for each
// tool with the arguments a test names, and the real agent dispatches it through the vault server.
public class McpAgentFileSystemTests(McpVaultServerFixture vaultFixture, RedisFixture redisFixture)
    : IClassFixture<McpVaultServerFixture>, IClassFixture<RedisFixture>, IAsyncLifetime
{
    private static readonly IReadOnlySet<string> _allFileSystemTools = FileSystemToolFeature.AllToolKeys;

    private FakeOpenRouter _model = null!;

    public async Task InitializeAsync() => _model = await FakeOpenRouter.StartAsync();

    public async Task DisposeAsync() => await _model.DisposeAsync();

    private McpAgent CreateAgent(OpenRouterChatClient llmClient)
    {
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, new RetentionSettings { PurgeHorizon = TimeSpan.FromMinutes(10) }, TimeProvider.System);
        return new McpAgent(
            TestAgentSpec.Default with
            {
                DisplayName = "test-fs-agent",
                McpServerEndpoints = [McpServerEndpoint.Configured(vaultFixture.McpEndpoint, McpTestSecret.Value)],
                FilesystemEnabledTools = _allFileSystemTools
            },
            llmClient,
            stateStore,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            [],
            // Signed as the deployment's agent signs, or every gated server refuses its calls.
            callTokens: McpTestSecret.CallTokens);
    }

    // The model calls this tool with these arguments, then answers whatever it was given.
    private async Task<List<AiResponse>> CallAsync(string tool, JsonObject arguments)
    {
        _model.Script(FakeOpenRouter.CallingToolNamed(tool, arguments.ToJsonString()));
        using var client = FakeTurn.Client(_model);
        await using var agent = CreateAgent(client);
        return await FakeTurn.RunAsync(agent, $"Use the {tool} tool.");
    }

    private string ToolAnswered => _model.Requests[^1].ToolOutputs.ShouldHaveSingleItem();

    [Fact]
    public async Task Agent_WithFileSystemFeature_CanReadFile()
    {
        vaultFixture.CreateFile("read-test.md", "# Secret Document\nThis is the content.");

        var responses = await CallAsync("domain__filesystem__file_read", new JsonObject { ["filePath"] = "/vault/read-test.md" });

        responses.ShouldNotBeEmpty();
        ToolAnswered.ShouldContain("Secret Document");
    }

    [Fact]
    public async Task Agent_WithFileSystemFeature_CanCreateFile()
    {
        await CallAsync("domain__filesystem__text_create",
            new JsonObject { ["filePath"] = "/vault/created-by-agent.md", ["content"] = "# Created\nHello from agent" });

        var filePath = Path.Combine(vaultFixture.VaultPath, "created-by-agent.md");
        File.Exists(filePath).ShouldBeTrue("Agent should have created the file");
        (await File.ReadAllTextAsync(filePath)).ShouldContain("Created");
    }

    [Fact]
    public async Task Agent_WithFileSystemFeature_CanEditFile()
    {
        vaultFixture.CreateFile("edit-test.md", "Hello World");

        await CallAsync("domain__filesystem__text_edit", new JsonObject
        {
            ["filePath"] = "/vault/edit-test.md",
            ["edits"] = new JsonArray(new JsonObject { ["oldString"] = "World", ["newString"] = "Agent" })
        });

        var content = await File.ReadAllTextAsync(Path.Combine(vaultFixture.VaultPath, "edit-test.md"));
        content.ShouldContain("Agent");
        content.ShouldNotContain("World");
    }

    [Fact]
    public async Task Agent_WithFileSystemFeature_CanSearchFiles()
    {
        vaultFixture.CreateFile(Path.Combine("search-test", "doc1.md"), "The quick brown fox jumps over the lazy dog.");
        vaultFixture.CreateFile(Path.Combine("search-test", "doc2.md"), "A different document without the target phrase.");

        await CallAsync("domain__filesystem__text_search",
            new JsonObject { ["directoryPath"] = "/vault/search-test", ["query"] = "quick brown fox" });

        ToolAnswered.ShouldContain("doc1");
        ToolAnswered.ShouldNotContain("doc2");
    }

    [Fact]
    public async Task Agent_WithFileSystemFeature_CanMoveFile()
    {
        vaultFixture.CreateFile(Path.Combine("move-src", "moveme.md"), "move content");
        Directory.CreateDirectory(Path.Combine(vaultFixture.VaultPath, "move-dst"));

        await CallAsync("domain__filesystem__move", new JsonObject
        {
            ["sourcePath"] = "/vault/move-src/moveme.md", ["destinationPath"] = "/vault/move-dst/moveme.md"
        });

        File.Exists(Path.Combine(vaultFixture.VaultPath, "move-dst", "moveme.md")).ShouldBeTrue();
        File.Exists(Path.Combine(vaultFixture.VaultPath, "move-src", "moveme.md")).ShouldBeFalse();
    }

    [Fact]
    public async Task Agent_WithFileSystemFeature_CanRemoveFile()
    {
        vaultFixture.CreateFile("remove-me.md", "to be deleted");

        await CallAsync("domain__filesystem__remove", new JsonObject { ["path"] = "/vault/remove-me.md" });

        File.Exists(Path.Combine(vaultFixture.VaultPath, "remove-me.md")).ShouldBeFalse();
    }
}