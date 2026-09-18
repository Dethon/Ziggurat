using JetBrains.Annotations;

namespace Domain.Tools.Web;

// The judgment's tunables, in the browse server's own appsettings alone: no other host has to
// agree, and each is a config edit with a probe run behind it. There is no enabled flag — an empty
// TypeSafe key is the feature off, and nothing downstream checks the key itself.
public sealed record ModalJudgmentSettings
{
    // From the moment the judge is asked, and paid only by a page that would otherwise keep its
    // overlay: the selector and word-list paths run first and cost nothing new. A warm judgment
    // measured a 325 ms median; this sits well above that tail and below what a browse notices.
    public int DeadlineMs { get; [UsedImplicitly] init; } = 1000;

    // A pick is clicked at this confidence; below it the page is left exactly as today.
    public double Confidence { get; [UsedImplicitly] init; } = 0.6;

    // The overlay's controls go to the judge in document order, up to this many.
    public int MaxControls { get; [UsedImplicitly] init; } = 20;
}