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
        var august = await QueryAsync("""
            select name, eaten_at from items
            where name ilike 'paella%' and eaten_at_local >= '2026-08-01' and eaten_at_local < '2026-09-01'
            """);

        august.ShouldHaveSingleItem().ShouldBe(["Paella valenciana", "2026-08-09T14:30:00+02:00"]);
    }

    // The doses a question about her insulin reads: the rapid she took for the last paella, linked
    // to it, and her basal that night, which belongs to no meal.
    [Fact]
    public async Task TheSeededDoses_AreReadUnderTheMount_TheLastPaellasLinkedToIt()
    {
        var rows = await QueryAsync("""
            select d.taken_at, d.kind, d.units, d.meal_id, m.items, m.outcome
            from doses d left join meals m using (meal_id)
            where d.taken_at_local::date = DATE '2026-08-09' order by d.taken_at
            """);

        rows[0].ShouldBe(["2026-08-09T14:30:00+02:00", "rapid", "7", "6b1f0c2e-0000-4000-8000-000000000103",
            "Paella valenciana", "high"]);
        rows.ShouldContain(row => row[1] == "basal" && row[2] == "14" && row[3] == null);
    }

    [Fact]
    public async Task TheSeededFoods_AreHers()
    {
        var names = (await QueryAsync("select name from foods")).Select(row => row[0]).ToList();

        names.ShouldContain("Pan de molde integral");
        names.ShouldContain("Yogur natural");
    }

    private async Task<List<string?[]>> QueryAsync(string sql)
    {
        var answer = await _server.Client.CallToolAsync("fs_query", new Dictionary<string, object?> { ["path"] = "/", ["sql"] = sql });
        var json = JsonNode.Parse(string.Join("", answer.Content.OfType<TextContentBlock>().Select(block => block.Text)))!;
        return [.. (json["rows"] ?? throw new InvalidOperationException(json.ToJsonString())).AsArray()
            .Select(row => row!.AsArray().Select(cell => cell?.ToString()).ToArray())];
    }
}