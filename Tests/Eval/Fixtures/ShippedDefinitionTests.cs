using Shouldly;
using Tests.Unit;

namespace Tests.Eval.Fixtures;

// A pass drives one definition from its first scenario to its last. The shipped file is read from
// the working tree, and an edit landing there while a pass is in flight — a routing tweak saved
// from an editor, a commit — must not reach the stacks that start afterwards, let alone be read
// half-written by one of them.
public class ShippedDefinitionTests
{
    [Fact]
    public void Settings_BoundOnce_DoNotFollowALaterEditOfTheFile()
    {
        using var files = new TemporaryFiles();
        File.WriteAllText(files.Settings, Settings("first"));
        File.WriteAllText(files.Agents, TestAgentsTests.Copies("jack"));
        var definition = new ShippedDefinition(files.Settings, new TestAgents(files.Agents));

        definition.Settings.Redis.ConnectionString.ShouldBe("first");

        File.WriteAllText(files.Settings, Settings("second"));

        definition.Settings.Redis.ConnectionString.ShouldBe("first");
        definition.Settings.ShouldBeSameAs(definition.Settings);
    }

    // The eval measures today's agents, so whatever the settings file carries — or, once the agents
    // are retired from it, does not — is not what a stack is built from.
    [Fact]
    public void Settings_TheAgents_AreTheCopiesNotTheFiles()
    {
        using var files = new TemporaryFiles();
        File.WriteAllText(files.Settings, Settings(
            "localhost",
            agents: """[{ "id": "jack", "name": "jack", "model": "z-ai/glm-5.3-flash", "mcpServerEndpoints": [] }]"""));
        File.WriteAllText(files.Agents, TestAgentsTests.Copies("nabu"));

        var settings = new ShippedDefinition(files.Settings, new TestAgents(files.Agents)).Settings;

        settings.Agents.Select(a => a.Id).Where(id => !id.StartsWith(ShippedDefinition.TemplatePrefix)).ShouldBe(["nabu"]);
        settings.Agents.ShouldContain(a => a.Id == ShippedDefinition.TemplatePrefix + "nabu");
        settings.DefaultAgent.ShouldBe("jack");
    }

    // The jonas template names the kit's Home Assistant and library, which a household that never set
    // them up does not reach; the eval hosts what a definition names, so its copy is that household's.
    [Fact]
    public void Settings_TheJonasTemplatesCopies_AreAHouseholdsWithNoHomeAndNoLibrary()
    {
        var agents = ShippedDefinition.Repository.Settings.Agents;

        foreach (var id in new[] { "template-jonas", "template-jonas-novoice" })
        {
            agents.Single(a => a.Id == id).McpServerEndpoints.ShouldNotContain("homeassistant");
            agents.Single(a => a.Id == id).McpServerEndpoints.ShouldNotContain("library");
            agents.Single(a => a.Id == id).McpServerEndpoints.ShouldContain("vault");
        }

        agents.Single(a => a.Id == "template-nabu").McpServerEndpoints.ShouldContain("homeassistant");
    }

    private static string Settings(string redis, string? agents = null) =>
        $$"""
            {
                "openRouter": { "apiUrl": "https://openrouter.ai/api/v1/", "apiKey": "" },
                "redis": { "connectionString": "{{redis}}" }{{(agents is null ? "" : $", \"agents\": {agents}")}}
            }
            """;

    private sealed class TemporaryFiles : IDisposable
    {
        public string Settings { get; } = Path.Combine(Path.GetTempPath(), $"eval-shipped-{Guid.NewGuid():N}.json");
        public string Agents { get; } = Path.Combine(Path.GetTempPath(), $"eval-agents-{Guid.NewGuid():N}.json");

        public void Dispose()
        {
            File.Delete(Settings);
            File.Delete(Agents);
        }
    }
}