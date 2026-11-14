namespace Domain.DTOs.Metrics;

public record TokenUsageEvent : MetricEvent
{
    // The name the call went by (a person's display name, Telegram's username): a label, never a key.
    public required string Sender { get; init; }

    // The account the call was billed to, which a person's figures are keyed by so a rename does not
    // split them; null for a call acting for no account (the guest, nobody, a fixture).
    public string? UserId { get; init; }

    public required string Model { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required decimal Cost { get; init; }

    // Prompt-cache hits, as reported by the provider. Null means the provider said nothing about
    // caching — deliberately not 0, so "no detail" stays distinguishable from "nothing was cached".
    // Without this the hit rate can only be inferred from cost against list pricing, which the
    // :nitro routing variants make unreliable.
    public long? CachedInputTokens { get; init; }
}