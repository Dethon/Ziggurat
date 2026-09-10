using Shouldly;

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
        var path = Path.Combine(Path.GetTempPath(), $"eval-shipped-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, Definition("jack"));
            var definition = new ShippedDefinition(path);

            definition.Settings.Agents.Select(a => a.Id).ShouldBe(["jack"]);

            File.WriteAllText(path, Definition("jack", "nabu"));

            definition.Settings.Agents.Select(a => a.Id).ShouldBe(["jack"]);
            definition.Settings.ShouldBeSameAs(definition.Settings);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Definition(params string[] agentIds)
    {
        var agents = string.Join(",", agentIds.Select(id =>
            $$"""{ "id": "{{id}}", "name": "{{id}}", "model": "z-ai/glm-5.3-flash", "mcpServerEndpoints": [] }"""));

        return $$"""
            {
                "openRouter": { "apiUrl": "https://openrouter.ai/api/v1/", "apiKey": "" },
                "redis": { "connectionString": "localhost" },
                "agents": [{{agents}}]
            }
            """;
    }
}