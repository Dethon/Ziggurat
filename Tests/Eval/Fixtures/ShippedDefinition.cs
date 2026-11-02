using Agent.Settings;
using Domain.Prompts;
using Microsoft.Extensions.Configuration;
using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// The shipped definition, bound once per process. Every stack of a pass starts from the same
// object: the file is read from the working tree, and an edit saved there while a pass is in
// flight — a routing tweak, a commit — used to reach whichever stacks started after it, or be
// read half-written by one of them and fail the scenario with an agent "not configured".
//
// The agents themselves are the eval's own copies (TestAgents), not the file's: what a pass
// measures is today's definitions, whatever a household's configuration says.
public sealed class ShippedDefinition(string path, TestAgents agents)
{
    public static ShippedDefinition Repository { get; } =
        new(Path.Combine(RepositoryRoot.Path, "Agent", "appsettings.json"), TestAgents.Repository);

    // An agent a household makes from a shipped template goes by an id of its own; the eval drives each
    // template as one, beside the home's copies, under the template's id with this in front.
    public const string TemplatePrefix = "template-";

    private readonly Lazy<TestAgentSettings> _settings =
        new(() => WithTemplates(agents.Into(Bind(path))), LazyThreadSafetyMode.ExecutionAndPublication);

    public TestAgentSettings Settings => _settings.Value;

    private static TestAgentSettings WithTemplates(TestAgentSettings settings) =>
        settings with
        {
            Agents = [.. settings.Agents, .. AgentTemplates.All.Select(t => t with { Id = TemplatePrefix + t.Id })]
        };

    // The secrets come from user secrets rather than from the environment the container would
    // have had; everything else is the file's.
    private static AgentSettings Bind(string path)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .AddUserSecrets<ShippedDefinition>()
            .AddEnvironmentVariables()
            .Build();

        return configuration.Get<AgentSettings>()
               ?? throw new InvalidOperationException($"{path} did not bind.");
    }
}