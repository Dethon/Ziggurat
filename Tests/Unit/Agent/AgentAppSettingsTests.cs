using System.Reflection;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Prompts;
using global::Agent.Settings;
using Infrastructure.Agents;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.Agent;

// Guards the appsettings -> definition -> prompt chain, and the cross-key rules that break a
// turn outright. Deliberately not the values the file happens to carry: reading a value back
// off the same file only fails when someone changes it on purpose, and then the test is edited
// to match -- friction, never a defect caught. What is left is the wiring that fails silently
// and the couplings no single reader would spot.
public class AgentAppSettingsTests
{
    // Two contracts for the same thing is how the drift got in: a relative rule sitting beside
    // the absolute one lets a short transcript surrounded by English resolve to English.
    [Fact]
    public void CustomInstructions_Nabu_CarryNoCompetingRelativeLanguageRule()
    {
        Agent("nabu")["customInstructions"]!.GetValue<string>()
            .ShouldNotContain("the language the user spoke");
    }

    // The rest of the chain: appsettings -> AgentDefinition -> system prompt. Every hop binds by
    // convention, so a renamed or dropped key fails nothing at build time -- the agent just
    // quietly goes back to inferring its language from an all-English request.
    [Fact]
    public void Language_Nabu_ReachesTheSystemPromptAsItsLastSection()
    {
        var nabu = BoundAgents().Single(a => a.Id == "nabu");
        nabu.Language.ShouldBe("es");

        McpAgent.BuildInstructions(
                name: nabu.Name,
                description: nabu.Description,
                customInstructions: nabu.CustomInstructions,
                language: nabu.Language,
                domainPrompts: [],
                fileSystemPrompts: [],
                clientPrompts: [],
                now: DateTimeOffset.UnixEpoch)
            .ShouldEndWith(LanguagePrompt.Build("es")!);
    }

    // The failure in this file's territory that nobody would notice. Configuration binds by naming
    // convention and nothing sets ErrorOnUnknownConfiguration, so renaming a property on
    // AgentDefinition leaves its JSON key silently ignored -- no error, no warning, the agent just
    // quietly runs on the default. Walking every declared key covers keys added later too, and
    // says nothing about which values they hold.
    [Fact]
    public void Binding_EveryKeyDeclaredInAppSettings_ReachesItsDefinition()
    {
        var agents = BoundAgents();
        var subAgents = BoundSubAgents();

        var severed = Declared("agents", id => agents.Single(a => a.Id == id))
            .Concat(Declared("subAgents", id => subAgents.Single(a => a.Id == id)))
            .Where(binding => binding.Bound is null)
            .Select(binding => binding.Path)
            .ToList();

        severed.ShouldBeEmpty();
    }

    // The migration exists to remove the dual-idiom problem; a pasted suffix would bring it back.
    [Fact]
    public void Model_NoAgentOrSubAgent_CarriesARoutingSuffix()
    {
        var models = Root()["agents"]!.AsArray()
            .Concat(Root()["subAgents"]!.AsArray())
            .Select(a => a!["model"]!.GetValue<string>());

        models.ShouldAllBe(m => !m.Contains(":nitro") && !m.Contains(":floor"));
    }

    // Neighboring binding style (see ProviderRoutingBindingTests): an in-memory IConfiguration
    // isolates the keys under test from the real appsettings.json, so a rename here fails loudly
    // instead of silently reading back whatever the file happens to carry.
    [Fact]
    public void Bind_PatchableModels_BindsIdAndName()
    {
        var settings = BindSettings(
            ("patchableModels:0:id", "openai/gpt-5.6-luna"),
            ("patchableModels:0:name", "GPT Luna"),
            ("patchableModels:1:id", "z-ai/glm-5.2"),
            ("patchableModels:1:name", "GLM 5.2"));

        settings.PatchableModels.ShouldBe([
            new PatchableModel("openai/gpt-5.6-luna", "GPT Luna"),
            new PatchableModel("z-ai/glm-5.2", "GLM 5.2")
        ]);
    }

    private static AgentSettings BindSettings(params (string Key, string? Value)[] entries)
    {
        var config = new Dictionary<string, string?>
        {
            ["openRouter:apiUrl"] = "https://openrouter.ai/api/v1/",
            ["openRouter:apiKey"] = "key",
            ["redis:connectionString"] = "redis:6379",
            ["agents:0:id"] = "agent",
            ["agents:0:name"] = "Agent",
            ["agents:0:model"] = "openai/gpt-5",
            ["agents:0:mcpServerEndpoints:0"] = "http://localhost"
        };
        foreach (var (key, value) in entries)
        {
            config[key] = value;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build()
            .Get<AgentSettings>()!;
    }

    private static AgentDefinition[] BoundAgents() =>
        BoundConfig().GetSection("agents").Get<AgentDefinition[]>()!;

    private static SubAgentDefinition[] BoundSubAgents() =>
        BoundConfig().GetSection("subAgents").Get<SubAgentDefinition[]>()!;

    private static IConfigurationRoot BoundConfig() =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "Agent", "appsettings.json"))
            .Build();

    // Every key each entry declares, paired with what the binder put on the matching definition.
    private static IEnumerable<(string Path, object? Bound)> Declared(
        string section, Func<string, object> definitionFor)
    {
        return Root()[section]!.AsArray()
            .Select(entry => entry!.AsObject())
            .SelectMany(entry =>
            {
                var definition = definitionFor(entry["id"]!.GetValue<string>());
                return entry.Select(key => ($"{entry["id"]}.{key.Key}", Bound(definition, key.Key)));
            });
    }

    private static object? Bound(object definition, string key) =>
        definition.GetType()
            .GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?.GetValue(definition);

    private static JsonNode Agent(string agentId) =>
        Root()["agents"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == agentId)!;

    private static JsonNode Root()
    {
        // Read the working tree, never AppContext.BaseDirectory: many referenced projects copy
        // their own appsettings.json to the test output, so Tests/bin/.../appsettings.json is
        // whichever one won the copy race -- not the Agent's. File.ReadAllText also strips the
        // UTF-8 BOM this file carries, which would otherwise fail JSON parsing.
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Agent", "appsettings.json"));
        return JsonNode.Parse(json)!;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Ziggurat.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("Ziggurat.sln not found above test directory");
    }
}