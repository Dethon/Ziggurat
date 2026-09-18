using Domain.Contracts;
using Domain.DTOs.Metrics;

namespace Infrastructure.Clients.Browser;

// How one detected overlay ended. The two cheap paths come first and cost nothing new; the
// judgment is asked only of an overlay both of them left standing; and left standing is the
// outcome the whole measure exists to count.
public enum ModalDismissalPath
{
    Selector,
    Text,
    Judgment,
    LeftStanding
}

public sealed record ModalOverlayOutcome(
    ModalType Kind,
    ModalDismissalPath Path,
    ModalDismissed? Dismissed = null,
    double? Confidence = null,
    TimeSpan? JudgmentLatency = null)
{
    public static ModalOverlayOutcome LeftStanding(ModalType kind) => new(kind, ModalDismissalPath.LeftStanding);

    public ModalDismissalEvent ToEvent() => new()
    {
        Kind = ModalKinds.Of(Kind),
        Outcome = Path switch
        {
            ModalDismissalPath.Selector => ModalDismissalOutcomes.Selector,
            ModalDismissalPath.Text => ModalDismissalOutcomes.Text,
            ModalDismissalPath.Judgment => ModalDismissalOutcomes.Judgment,
            ModalDismissalPath.LeftStanding => ModalDismissalOutcomes.LeftStanding,
            _ => throw new ArgumentOutOfRangeException(nameof(Path), Path, "A path with no wire spelling")
        },
        Selector = Dismissed?.Selector,
        ButtonText = Dismissed?.ButtonText,
        Confidence = Confidence,
        DurationMs = JudgmentLatency is { } latency ? (long)latency.TotalMilliseconds : null
    };
}