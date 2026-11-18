namespace McpServerSugary.Store;

// A glucose watch of the household: its id is the household's, and it is about one person's sensor
// (SubjectUserId), written by somebody (WriterUserId) — not a row of anyone's log, so IHousehold rather
// than IPersonal, and who is shown it is decided where it is read (SqliteGlucoseWatches), not by a
// filter. Spec is the watch.json as last written (deliverTo and userId filled, no trigger's person);
// Enabled and Once are the spec's own, kept as columns for the engine to ask.
public class GlucoseWatch : IHousehold
{
    public string TenantId { get; set; } = "";
    public string Id { get; set; } = "";

    public string SubjectUserId { get; set; } = "";
    public string WriterUserId { get; set; } = "";
    public string AgentId { get; set; } = "";

    public string Spec { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Once { get; set; }

    // The channels a fire is delivered to, as a JSON list; null for none named.
    public string? DeliverTo { get; set; }

    // Who a fire runs as.
    public string? UserId { get; set; }

    // The delegation the agent signed for the write that last set it (ticket 12 fills it).
    public string? Delegation { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastFiredAt { get; set; }
    public bool Spent { get; set; }
}

// What the engine judged of one trigger of a watch, so a restart neither fires an episode again nor
// forgets a `for` already running: whether its condition holds, since when, and whether this episode
// fired. A trigger is known by its place in the watch's list, so writing the watch anew forgets its rows.
public class GlucoseWatchState : IHousehold
{
    public string TenantId { get; set; } = "";
    public string WatchId { get; set; } = "";
    public int TriggerIndex { get; set; }

    public bool Holding { get; set; }
    public DateTimeOffset? HoldingSince { get; set; }
    public bool FiredThisEpisode { get; set; }
}