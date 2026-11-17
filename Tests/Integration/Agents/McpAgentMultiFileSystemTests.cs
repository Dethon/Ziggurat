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

// Two filesystems mounted side by side, each reached by the tool calls the fake model makes.
public class McpAgentMultiFileSystemTests(MultiFileSystemFixture fsFixture, RedisFixture redisFixture)
    : IClassFixture<MultiFileSystemFixture>, IClassFixture<RedisFixture>, IAsyncLifetime
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
                DisplayName = "test-multi-fs-agent",
                McpServerEndpoints =
                [
                    McpServerEndpoint.Configured(fsFixture.LibraryEndpoint, McpTestSecret.Value),
                    McpServerEndpoint.Configured(fsFixture.NotesEndpoint, McpTestSecret.Value)
                ],
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

    private async Task<List<AiResponse>> RunAsync(string prompt)
    {
        using var client = FakeTurn.Client(_model);
        await using var agent = CreateAgent(client);
        return await FakeTurn.RunAsync(agent, prompt);
    }

    private static Func<JsonObject, string> Calling(string tool, JsonObject arguments) =>
        FakeOpenRouter.CallingToolNamed(tool, arguments.ToJsonString());

    [Fact]
    public async Task Agent_WithMultipleFileSystems_CanReadFromBoth()
    {
        fsFixture.CreateLibraryFile("multi-read.md", "Library content alpha");
        fsFixture.CreateNotesFile("multi-read.md", "Notes content bravo");
        _model.Script(
            Calling("domain__filesystem__file_read", new JsonObject { ["filePath"] = "/library/multi-read.md" }),
            Calling("domain__filesystem__file_read", new JsonObject { ["filePath"] = "/notes/multi-read.md" }));

        var responses = await RunAsync("Read both files.");

        responses.ShouldNotBeEmpty();
        var read = string.Join(" ", _model.Requests[^1].ToolOutputs);
        read.ShouldContain("alpha");
        read.ShouldContain("bravo");
    }

    [Fact]
    public async Task Agent_WithMultipleFileSystems_CanCreateOnEach()
    {
        _model.Script(
            Calling("domain__filesystem__text_create", new JsonObject { ["filePath"] = "/library/multi-create.md", ["content"] = "library file" }),
            Calling("domain__filesystem__text_create", new JsonObject { ["filePath"] = "/notes/multi-create.md", ["content"] = "notes file" }));

        await RunAsync("Create both files.");

        var libraryFile = Path.Combine(fsFixture.LibraryPath, "multi-create.md");
        File.Exists(libraryFile).ShouldBeTrue("File should exist in library filesystem");
        (await File.ReadAllTextAsync(libraryFile)).ShouldContain("library");

        var notesFile = Path.Combine(fsFixture.NotesPath, "multi-create.md");
        File.Exists(notesFile).ShouldBeTrue("File should exist in notes filesystem");
        (await File.ReadAllTextAsync(notesFile)).ShouldContain("notes");
    }

    // What the model is told, rather than what one model chose to repeat of it.
    [Fact]
    public async Task Agent_WithMultipleFileSystems_KnowsAvailableMountPoints()
    {
        await RunAsync("Which filesystems can you reach?");

        var told = _model.Requests[^1].SystemPrompt.ShouldNotBeNull();
        told.ShouldContain("/library");
        told.ShouldContain("/notes");
    }
}