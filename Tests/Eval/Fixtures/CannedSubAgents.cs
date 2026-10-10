using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.DTOs;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// The worker, stubbed. What a delegation scenario asks is whether the parent decided to delegate,
// which profile it named, and whether the prompt it wrote carries what the task needs — the worker
// has no conversation history, so anything the parent left out is simply gone. A real worker would
// put a second model's answer between that question and its assertion, and cost a run to do it.
public sealed class CannedSubAgents(string result) : ISubAgentSpawner
{
    private readonly Lock _gate = new();
    private readonly List<Delegation> _delegations = [];

    public IReadOnlyList<Delegation> Delegations
    {
        get
        {
            lock (_gate)
            {
                return [.. _delegations];
            }
        }
    }

    public DisposableAgent Spawn(SubAgentDefinition definition) =>
        new CannedAgent(definition.Id, result, Record);

    private void Record(Delegation delegation)
    {
        lock (_gate)
        {
            _delegations.Add(delegation);
        }
    }

    private sealed class CannedSession : AgentSession;

    private sealed class CannedAgent(string profileId, string result, Action<Delegation> record)
        : DisposableAgent
    {
        public override string Name => profileId;

        public override string Description => "A canned worker.";

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public override ValueTask DisposeThreadSessionAsync(AgentSession thread) => ValueTask.CompletedTask;

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<AgentSession>(new CannedSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? options = null, CancellationToken ct = default) =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serialized, JsonSerializerOptions? options = null, CancellationToken ct = default) =>
            ValueTask.FromResult<AgentSession>(new CannedSession());

        // The prompt is recorded here rather than in the spawner because it arrives with the run,
        // not with the profile: the parent chooses the worker first and writes the task second.
        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? thread = null,
            AgentRunOptions? options = null, CancellationToken ct = default)
        {
            record(new Delegation(profileId, string.Join("\n", messages.Select(m => m.Text))));

            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, result)));
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? thread = null,
            AgentRunOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException("A delegated run is never streamed by the tool that spawns it.");
    }
}

// One delegation: the profile the parent named and the prompt it wrote.
public sealed record Delegation(string ProfileId, string Prompt)
{
    // What a real worker came back with. Null for a canned one, whose answer is the scenario's
    // own string and says nothing about the run.
    public string? Answer { get; init; }

    // The delegations of a run with real workers, read off the parent's own calls: nothing stands
    // where the worker would have been, so the profile and the prompt are the call's arguments
    // and the answer is its result — the tool's envelope whole where the worker never answered,
    // because a timeout is what the parent read and had to act on.
    public static IReadOnlyList<Delegation> Heard(IEnumerable<ToolInvocation> calls) =>
    [
        .. calls
            .Where(call => call.ToolName == EvalTools.Subagent)
            .Select(call => new Delegation(Text(call.Arguments, "subAgentId") ?? "", Text(call.Arguments, "prompt") ?? "")
            {
                Answer = Text(call.Result, "result") ?? call.Result ?? call.Error
            })
    ];

    private static string? Text(string? json, string property)
    {
        try
        {
            return json is not null
                   && JsonNode.Parse(json) is JsonObject node
                   && node[property] is JsonValue value
                   && value.TryGetValue<string>(out var text)
                ? text
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}