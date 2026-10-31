using Agent.Settings;
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

    private readonly Lazy<AgentSettings> _settings =
        new(() => agents.Into(Bind(path)), LazyThreadSafetyMode.ExecutionAndPublication);

    public AgentSettings Settings => _settings.Value;

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