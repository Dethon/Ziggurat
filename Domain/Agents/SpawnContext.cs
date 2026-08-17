namespace Domain.Agents;

// What the parent contributes to a spawn. Every field is "what the parent was" rather than
// anything the subagent's own definition says, which is why they travel as one value: the next
// such value is a field here rather than a sixth positional parameter on the factory.
//
// Nothing binds this from configuration, so it lives beside the agent key rather than among the
// definitions in Domain.DTOs.
public sealed record SpawnContext(
    string ConversationId,
    string UserId,
    string[] WhitelistPatterns,
    // The parent's own opt-in, and the ceiling on the subagent's: a subagent acts on the
    // parent's behalf, so it can never reach a machine the parent could not.
    bool UsesOutposts);