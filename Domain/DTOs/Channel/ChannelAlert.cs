using Domain.DTOs.Voice;
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// An insistent alert a server raises for one person — a timer with no satellite to ring on — and the
// agent rings wherever that person is. Only the agent holds the channels and their credentials, so
// the server puts it in its inbox like a fire; the agent offers it on every channel that can show
// one (offer_alert) and settles it everywhere on the first dismissal (settle_alert), as an approval
// settles (AlertRelay). No turn is run and no model is asked.
[PublicAPI]
public record ChannelAlertNotification
{
    // A whole GUID, 32 hex characters: the id every channel shows it under and a dismissal names,
    // which on Telegram rides a button's 64 bytes of callback data.
    public required string AlertId { get; init; }

    // The person it rings for, as the verified call that set it named them: whose chat clients are
    // told, whose Telegram private chat it is put in, and whose dismissal counts.
    public required Account Owner { get; init; }

    // What the person is told: the timer's text, or its id when it was given none.
    public required string Text { get; init; }

    public AnnounceKind Kind { get; init; } = AnnounceKind.Timer;

    // The agent of the turn that set it, which on Telegram says which bot speaks to the person.
    public string? AgentId { get; init; }
}

// The alert was stopped where it was raised — ./dismiss ran — so wherever it still shows, it stops.
[PublicAPI]
public record ChannelAlertStoppedNotification
{
    public required string AlertId { get; init; }
}

// What a channel's server raised about an alert, as the agent reads it off the channel: one to
// ring, or one it stopped.
[PublicAPI]
public abstract record AlertSignal
{
    public sealed record Ring(ChannelAlertNotification Alert) : AlertSignal;

    public sealed record Stop(string AlertId) : AlertSignal;
}

// An alert offered to its person on a channel. Answered "dismissed" when they dismissed it there;
// "no_answer" when it could not be shown, or was settled or called off before they did.
[PublicAPI]
public record OfferAlertParams
{
    public required string AlertId { get; init; }
    public required Account Owner { get; init; }
    public required string Text { get; init; }
    public AnnounceKind Kind { get; init; } = AnnounceKind.Timer;
    public string? AgentId { get; init; }
}

// The alert ended elsewhere: whatever still shows it here stops. Dismissed says whether a person
// dismissed it (on another channel, or with ./dismiss) or it rang out with nobody answering.
[PublicAPI]
public record SettleAlertParams
{
    public required string AlertId { get; init; }
    public bool Dismissed { get; init; }
}