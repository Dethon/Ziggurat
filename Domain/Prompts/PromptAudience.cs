namespace Domain.Prompts;

// Who a section is for. Empty means everyone, which is what almost every section is — naming an
// audience is how a section says it is NOT general, and the assembly drops it for anyone else.
//
// Agents are filtered at assembly. Channels are never a per-turn filter, on purpose: the
// instructions are the cached prefix of every request in a conversation, and a conversation can be
// mirrored across channels (voice opens it, WebChat continues it), so swapping sections per message
// would throw the cached prefix away mid-conversation and change the rules under the same thread.
// A channel's sections are read by the agent its conversation group is built for that channel
// (PromptManifest.ForChannel, IAgentFactory.CreateAsync's channelId), decided once by the turn that
// opens the group and kept for the group's life — whichever agent a household has answer aloud. A
// conversation continued on another channel after its group ended is built for that channel, and
// answers in the shape its reader there wants.
public sealed record PromptAudience
{
    public IReadOnlyList<string> Agents { get; init; } = [];

    public IReadOnlyList<string> Channels { get; init; } = [];

    public static readonly PromptAudience Everyone = new();

    public static PromptAudience ForAgents(params string[] agents) => new() { Agents = agents };

    public static PromptAudience ForChannels(params string[] channels) => new() { Channels = channels };

    public bool Includes(string agentId) =>
        Agents.Count == 0 || Agents.Any(a => a.Equals(agentId, StringComparison.OrdinalIgnoreCase));
}