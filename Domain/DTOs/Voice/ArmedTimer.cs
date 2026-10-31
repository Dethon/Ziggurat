using Domain.DTOs.Channel;

namespace Domain.DTOs.Voice;

public record ArmedTimer
{
    public required string Id { get; init; }

    // The household whose timer it is, from the verified call that armed it: what lists, reads and
    // dismisses it, and whose voice server rings it. Ids are the model's to choose, so two
    // households may each hold a timer of the same id.
    public required string TenantId { get; init; }

    // The person whose turn armed it — none for the household's guest — on whose channels a timer
    // with no satellite target rings.
    public Account? Owner { get; init; }

    // The agent of the turn that armed it, which on Telegram says which bot speaks to the owner.
    public string? AgentId { get; init; }

    public string? Text { get; init; }

    // The satellites it rings on; none rings it on its owner's channels instead.
    public AnnounceTarget? Target { get; init; }

    public required int DurationSeconds { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime FiresAtUtc { get; init; }
}