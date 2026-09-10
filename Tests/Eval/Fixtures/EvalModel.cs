using Agent.Settings;
using Domain.DTOs;

namespace Tests.Eval.Fixtures;

// The model a pass drives, when it is not the shipped one.
//
// A parity run compares two models against one definition, and the only way to switch used to be
// editing Agent/appsettings.json — a working-tree change that a `git add -A` swept into a commit
// once already. Two variables instead: the model, applied to every agent and subagent so the
// workers run on the same one as the parent, and optionally the provider to pin it to.
public static class EvalModel
{
    public const string Variable = "ZIGGURAT_EVAL_MODEL";
    public const string ProviderVariable = "ZIGGURAT_EVAL_PROVIDER";

    public static AgentSettings FromEnvironment(AgentSettings shipped) =>
        Apply(
            shipped,
            Environment.GetEnvironmentVariable(Variable),
            Environment.GetEnvironmentVariable(ProviderVariable));

    public static AgentSettings Apply(AgentSettings shipped, string? model, string? provider)
    {
        var chosen = Trimmed(model);
        var pinned = Trimmed(provider);

        if (chosen is null && pinned is null)
        {
            return shipped;
        }

        return shipped with
        {
            Agents =
            [
                .. shipped.Agents.Select(agent => agent with
                {
                    Model = chosen ?? agent.Model,
                    ProviderRouting = Repinned(agent.ProviderRouting, chosen, pinned)
                })
            ],
            SubAgents =
            [
                .. shipped.SubAgents.Select(worker => worker with
                {
                    Model = chosen ?? worker.Model,
                    ProviderRouting = Repinned(worker.ProviderRouting, chosen, pinned)
                })
            ]
        };
    }

    // A shipped `only` or `order` names who serves the shipped model, so under an override it is
    // either replaced by the provider asked for or lifted — kept, it would refuse every endpoint
    // the other model has. Everything else the routing says (the sort, its thresholds, the ignore
    // list) is the deployment's preference regardless of model and travels on unchanged. Routing
    // is still enforced on every request: what changes is the object, not whether it is sent.
    private static ProviderRouting? Repinned(ProviderRouting? shipped, string? model, string? provider)
    {
        if (provider is not null)
        {
            return (shipped ?? new ProviderRouting()) with { Only = [provider], Order = null };
        }

        return model is null || shipped is null
            ? shipped
            : shipped with { Only = null, Order = null };
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}