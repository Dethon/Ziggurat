using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Microsoft.Extensions.AI;

namespace Domain.Tools.SubAgents;

public class SubAgentRunTool(
    SubAgentRegistryOptions registryOptions,
    FeatureConfig featureConfig)
{
    public const string Name = "run_subagent";

    private readonly SubAgentDefinition[] _profiles = registryOptions.SubAgents;

    public string Description
    {
        get
        {
            var profileList = string.Join("\n",
                _profiles.Select(p => $"- \"{p.Id}\": {p.Description ?? p.Name}"));
            return $"""
                    Runs a task on a subagent with a fresh context and returns the result.
                    Available subagents:
                    {profileList}
                    """;
        }
    }

    public async Task<JsonNode> RunAsync(
        [Description("ID of the subagent profile to use")]
        string subAgentId,
        [Description("The task/prompt to send to the subagent")]
        string prompt,
        CancellationToken ct = default)
    {
        var profile = _profiles.FirstOrDefault(p =>
            p.Id.Equals(subAgentId, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return ToolError.Create(
                ToolError.Codes.NotFound,
                $"Unknown subagent: '{subAgentId}'. Available: {string.Join(", ", _profiles.Select(p => p.Id))}");
        }

        if (featureConfig.SubAgentFactory is null)
        {
            // Not a dependency that is down — a run this tool was handed without the means to spawn
            // anything, which is how a harness or a subagent's own run reaches here. It will be
            // exactly as unavailable on the next call, so the answer must not invite one.
            return CapabilityError.For(
                CapabilityState.Unassigned,
                "Subagent execution was not granted to this run",
                "Do the work in this turn rather than delegating it; retrying will not change this.")
                .ToNode();
        }

        try
        {
            await using var agent = featureConfig.SubAgentFactory(profile);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(profile.MaxExecutionSeconds));

            var userMessage = new ChatMessage(ChatRole.User, prompt);
            userMessage.SetSenderId(featureConfig.UserId);
            // The PARENT's context, verbatim -- never the subagent's own id. Downstream MCP
            // servers scope per-conversation state by {AgentId}:{ConversationId}, so a
            // file_search issued by the parent and a file_download issued by the subagent it
            // spawned have to resolve to the same scope. A subagent acts on the parent's
            // behalf, which makes parent attribution the correct one, not a shortcut.
            var context = featureConfig.ConversationContextProvider?.Invoke();
            userMessage.SetConversationContext(context);
            // A worker runs its parent's model, and on a turn the person moved to another one
            // that is the model the turn asked for. The worker's request is new and carries no
            // patch, so the turn's is written onto it; the worker resolves it against the same
            // whitelist its parent did.
            if (context?.ConfigPatchModel is { } turnModel)
            {
                userMessage.SetConfigPatch(new AgentConfigPatch { Model = turnModel });
            }

            var response = await agent.RunAsync(
                [userMessage], cancellationToken: timeoutCts.Token);

            return new JsonObject
            {
                ["status"] = "completed",
                ["result"] = response.Text
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolError.Create(
                ToolError.Codes.Timeout,
                $"Subagent '{profile.Id}' exceeded its maximum execution time of {profile.MaxExecutionSeconds}s");
        }
        catch (Exception ex)
        {
            return ToolError.Create(
                ToolError.Codes.InternalError,
                ex.Message);
        }
    }
}