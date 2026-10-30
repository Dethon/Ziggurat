using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record Schedule
{
    public required string Id { get; init; }
    public required string AgentId { get; init; }
    public required string Prompt { get; init; }
    public string? CronExpression { get; init; }
    public DateTime? RunAt { get; init; }
    public string? UserId { get; init; }

    // Whose turn last wrote it: the principal the call's token named, recorded from that turn's own
    // context and never from the JSON, whose userId is the model's to write. A record of who, and what
    // redelegate offers a schedule nobody has delegated; never what a fire acts as.
    public Principal? CreatedBy { get; init; }

    // What the schedule's fire acts as: the delegation the agent signed for the write that last
    // created, edited or moved it, kept as the call carried it. Null when that write carried none —
    // a turn acting for nobody — and the fire then acts for nobody.
    public string? Delegation { get; init; }
    public IReadOnlyList<string>? DeliverTo { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastRunAt { get; init; }
    public DateTime? NextRunAt { get; init; }
}