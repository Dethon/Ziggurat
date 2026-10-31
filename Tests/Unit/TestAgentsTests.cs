using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit;

// The agents the eval, the prompt snapshots and the WebChat stack drive are the tests' own copies,
// so retiring the definitions from the agent's settings file changes nothing they measure. Until
// that retirement the shipped file still carries them, and a copy that drifted from it would have
// the eval grading an agent nobody runs.
public class TestAgentsTests
{
    private static readonly string[] _sections = ["agentDefaults", "agents", "subAgents"];

    [Fact]
    public void TheCopies_EqualTodaysShippedDefinitions()
    {
        var copies = Parse(TestAgents.Repository.Path);
        var shipped = Parse(Path.Combine(TestHelpers.FindSolutionRoot(), "Agent", "appsettings.json"));

        copies.Select(section => section.Key).Order().ShouldBe(_sections.Order());
        _sections.ShouldAllBe(section => JsonNode.DeepEquals(copies[section], shipped[section]));
    }

    [Fact]
    public void TheCopies_BindEveryAgentAndWorker()
    {
        TestAgents.Repository.Agents.Select(a => a.Id).ShouldBe(["jack", "jonas", "nabu", "sugary"]);
        TestAgents.Repository.SubAgents.Select(a => a.Id).ShouldBe(["jonas-worker"]);
        TestAgents.Repository.Defaults.For("voice").ShouldBe("nabu");
    }

    // Every WebChat collection's stack splices the sugary agent through this, so a shape it cannot
    // read fails them all at start-up rather than the one test that needs it.
    [Fact]
    public void Json_TheSugaryAgent_IsItsDefinitionAsTheCopiesHoldIt()
    {
        var agent = JsonNode.Parse(TestAgents.Repository.Json("sugary"))!;

        agent["id"]!.GetValue<string>().ShouldBe("sugary");
        agent["model"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        agent["mcpServerEndpoints"]!.AsArray().Select(e => e!.GetValue<string>())
            .ShouldBe(["http://mcp-sugary:8080/mcp"]);
    }

    // A pass reads its agents once: an edit saved to the copies while it is in flight must not
    // reach the stacks that start afterwards, let alone be read half-written by one of them.
    [Fact]
    public void Agents_BoundOnce_DoNotFollowALaterEditOfTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test-agents-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, Copies("jack"));
            var agents = new TestAgents(path);

            agents.Agents.Select(a => a.Id).ShouldBe(["jack"]);

            File.WriteAllText(path, Copies("jack", "nabu"));

            agents.Agents.Select(a => a.Id).ShouldBe(["jack"]);
            agents.Agents.ShouldBeSameAs(agents.Agents);
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static string Copies(params string[] agentIds)
    {
        var agents = string.Join(",", agentIds.Select(id =>
            $$"""{ "id": "{{id}}", "name": "{{id}}", "model": "z-ai/glm-5.3-flash", "mcpServerEndpoints": [] }"""));

        return $$"""{ "agentDefaults": { "fallback": "jack" }, "agents": [{{agents}}], "subAgents": [] }""";
    }

    private static JsonObject Parse(string path) =>
        JsonNode.Parse(
            File.ReadAllText(path),
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
            })!.AsObject();
}