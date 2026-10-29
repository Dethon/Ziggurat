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

    // Who the schedule runs as: the principal of the turn that last wrote it, recorded from that
    // turn's own context and never from the JSON, whose userId is the model's to write.
    public Principal? CreatedBy { get; init; }
    public IReadOnlyList<string>? DeliverTo { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastRunAt { get; init; }
    public DateTime? NextRunAt { get; init; }
}