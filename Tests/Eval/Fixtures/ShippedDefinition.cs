using Agent.Settings;
using Domain.DTOs;
using Domain.DTOs.Household;
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

    // A template's agent as a household that set up no voice server runs it: nothing of the
    // household's can ring a countdown, so the timers are withheld as its features would withhold a
    // server (SharedServers.WithheldBy), and a countdown it asks for is a scheduled task.
    public const string NoVoiceSuffix = "-novoice";

    private readonly Lazy<TestAgentSettings> _settings =
        new(() => WithTemplates(agents.Into(Bind(path))), LazyThreadSafetyMode.ExecutionAndPublication);

    public TestAgentSettings Settings => _settings.Value;

    private static TestAgentSettings WithTemplates(TestAgentSettings settings)
    {
        var rangNowhere = settings.SharedServers.WithheldBy(new FeaturesSection { Idealista = true }, ringsNowhere: true);
        return settings with
        {
            Agents =
            [
                .. settings.Agents,
                .. AgentTemplates.All.Select(t => t with { Id = TemplatePrefix + t.Id }),
                .. AgentTemplates.All.Select(t => t with
                {
                    Id = TemplatePrefix + t.Id + NoVoiceSuffix,
                    McpServerEndpoints = [.. t.McpServerEndpoints.Where(e => !SharedServers.Names(rangNowhere, e))]
                })
            ]
        };
    }

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