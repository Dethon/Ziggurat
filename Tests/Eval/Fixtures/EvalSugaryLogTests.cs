using System.Text.Json.Nodes;
using Domain.Prompts;
using McpServerSugary.Modules;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Eval.Fixtures;

// The Sugary server as the sugary agent's runs find it, without a model: its estimating prompt and
// its skill served, and her seeded log readable under the mount. A stack that lost either would
// have a paid pass measure an agent with no estimating prompt, or a log question with no log.
public sealed class EvalSugaryLogTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"eval-sugary-test-{Guid.NewGuid():N}");
    private RunningServer _server = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _server = await InMemoryMcpServer.StartAsync(services => services.ConfigureSugary(EvalSugaryLog.Settings(_directory)));
        await EvalSugaryLog.SeedAsync(_server.App.Services);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task TheServer_ServesTheEstimatingPrompt()
    {
        var prompts = await _server.Client.ListPromptsAsync();

        prompts.Select(prompt => prompt.Name).ShouldContain(SugaryPrompt.Name);
    }

    [Fact]
    public async Task TheSeededLog_IsReadUnderTheMount_LastPaellaInAugust()
    {
        var text = await ReadAsync("/items/2026-08.csv");

        text.ShouldContain("Paella valenciana");
        text.ShouldContain("2026-08-09T14:30:00+02:00");
        text.ShouldNotContain("Paella de marisco");
    }

    // The doses a question about her insulin reads: the rapid she took for the last paella, linked
    // to it, and her basal that night, which belongs to no meal.
    [Fact]
    public async Task TheSeededDoses_AreReadUnderTheMount_TheLastPaellasLinkedToIt()
    {
        var text = await ReadAsync("/doses/2026-08.csv");

        text.ShouldContain("2026-08-09T14:30:00+02:00,rapid,7,6b1f0c2e-0000-4000-8000-000000000103,");
        text.ShouldContain(",basal,14,,");
        (await ReadAsync("/meals/2026-08.csv")).ShouldContain(",high,Paella valenciana,");
    }

    [Fact]
    public async Task TheSeededFoods_AreHers()
    {
        var text = await ReadAsync("/foods.csv");

        text.ShouldContain("Pan de molde integral");
        text.ShouldContain("Yogur natural");
    }

    private async Task<string> ReadAsync(string path)
    {
        var read = await _server.Client.CallToolAsync("fs_read", new Dictionary<string, object?> { ["path"] = path });
        var json = JsonNode.Parse(string.Join("", read.Content.OfType<TextContentBlock>().Select(block => block.Text)))!;
        return json["content"]!.GetValue<string>();
    }
}