using Domain.Contracts;

namespace Domain.DTOs.Metrics;

// One overlay the browser detected on a navigation, and how it ended: closed by a selector, by a
// word in its button, by a judgment over what its buttons say, or left standing. A navigation with
// no overlay publishes nothing. This is the miss rate as a number, before and after a change to
// the word lists or to the questions the judge is asked.
public record ModalDismissalEvent : MetricEvent
{
    // One of ModalKinds: cookie, age, newsletter, notification, generic.
    public required string Kind { get; init; }

    // One of ModalDismissalOutcomes: selector, text, judgment, left-standing.
    public required string Outcome { get; init; }

    // The path that closed it, as the browse envelope names it: a CSS selector, `text(<word>)`
    // or `judgment(<index>)`.
    public string? Selector { get; init; }

    public string? ButtonText { get; init; }

    // Set only where a judgment was asked: the judge's confidence in what was clicked (or in the
    // answer that clicked nothing), and how long it took to answer.
    public double? Confidence { get; init; }

    public long? DurationMs { get; init; }
}

// The wire spellings, the ones the dashboard groups by and the spec names.
public static class ModalDismissalOutcomes
{
    public const string Selector = "selector";
    public const string Text = "text";
    public const string Judgment = "judgment";
    public const string LeftStanding = "left-standing";
}

public static class ModalKinds
{
    public const string Cookie = "cookie";
    public const string Age = "age";
    public const string Newsletter = "newsletter";
    public const string Notification = "notification";
    public const string Generic = "generic";

    public static string Of(ModalType type) => type switch
    {
        ModalType.CookieConsent => Cookie,
        ModalType.AgeGate => Age,
        ModalType.Newsletter => Newsletter,
        ModalType.Notification => Notification,
        ModalType.Generic => Generic,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "A modal type with no wire spelling")
    };
}