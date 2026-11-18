namespace Domain.DTOs.Sugary;

// A glucose watch as Sugary keeps it. Spec is the watch.json as last written, deliverTo and userId filled
// and every trigger's person left out: whose sensor it watches is SubjectUserId, which a read names again
// in the reader's own words. Enabled and Once are the spec's, kept beside it for the engine to ask.
public sealed record StoredGlucoseWatch
{
    public required string Id { get; init; }
    public required string SubjectUserId { get; init; }

    // Who wrote it first; a later edit by somebody else leaves it, so the writer keeps seeing what they set up.
    public required string WriterUserId { get; init; }

    // The agent that created it, which runs its prompts.
    public required string AgentId { get; init; }

    public required string Spec { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Once { get; init; }
    public IReadOnlyList<string>? DeliverTo { get; init; }

    // Who a fire runs as.
    public string? UserId { get; init; }

    // The delegation the agent signed for the write that last set it; null until delegated writes reach
    // sugary-watches.
    public string? Delegation { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastFiredAt { get; init; }

    // A one-shot that fired, left off until written armed again.
    public bool Spent { get; init; }
}

// What the engine judged of one trigger of a watch: whether its condition holds, since when, and whether
// this episode of it already fired.
public sealed record GlucoseTriggerCondition(int TriggerIndex, bool Holding, DateTimeOffset? HoldingSince, bool FiredThisEpisode);