using Domain.Agents;
using Domain.DTOs.Channel;

namespace WebChat.Client.State.AgentSettings;

public static class AgentSettingsSelectors
{
    // Whether a model runs on the Lemonade chat host, read off the id's namespace: the catalogue
    // carries no provider field, and the marker a person sees is the same convention routing uses.
    public static bool IsLemonadeModel(string? modelId) => LemonadeModelId.IsLemonade(modelId);

    public static string? ModelNameFor(AgentCatalogEntry agent, string? modelId) =>
        modelId is null
            ? null
            : agent.PatchableModels?.FirstOrDefault(m => m.Id == modelId)?.Name ?? modelId;

    public static AgentConfigPatch? GetConfigPatch(
        AgentSettingsState state, IReadOnlyList<AgentCatalogEntry> agents, string agentId)
    {
        var agent = agents.FirstOrDefault(a => a.Id == agentId);
        var settings = state.ByAgent.GetValueOrDefault(agentId);
        if (agent is null || settings is null)
        {
            return null;
        }

        var model = Differs(settings.Model, agent.DefaultModel) ? settings.Model : null;
        var effort = Differs(settings.ReasoningEffort, agent.DefaultReasoningEffort)
            ? settings.ReasoningEffort
            : null;

        return model is null && effort is null
            ? null
            : new AgentConfigPatch { Model = model, ReasoningEffort = effort };
    }

    // The model a person picked is theirs and is never edited here. A model the catalogue stopped
    // listing used to be swapped for the agent's default, which quietly changed what answered:
    // most sharply when the Lemonade chat host goes down, because discovery fails closed and
    // empties every one of its models at once, so the next turn went to a hosted provider with no
    // patch at all — unrefused, and extracted from. Keeping it sends it, and the server says what
    // it is: a Lemonade model it cannot serve fails the turn by name, a hosted one it does not
    // offer warns and answers on the agent's own. See docs/adr/0042.
    //
    // Effort still falls back. It is a small fixed vocabulary the server warns and continues on,
    // so a stale one costs nothing, and no host disappears underneath it.
    public static AgentModelSettings Sanitize(AgentModelSettings settings, AgentCatalogEntry agent)
    {
        var effortValid = settings.ReasoningEffort is { } effort &&
                          (agent.PatchableReasoningEfforts ?? []).Contains(
                              effort, StringComparer.OrdinalIgnoreCase);

        // No model stored is not a stale pick — it is a client that has never chosen — so it
        // still takes the agent's own, which is what the menu shows as selected.
        return new AgentModelSettings(
            settings.Model ?? agent.DefaultModel,
            effortValid ? settings.ReasoningEffort : agent.DefaultReasoningEffort);
    }

    private static bool Differs(string? selected, string? fallback) =>
        selected is not null && !string.Equals(selected, fallback, StringComparison.OrdinalIgnoreCase);
}