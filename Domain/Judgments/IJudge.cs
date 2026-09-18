namespace Domain.Judgments;

// A typed judgment: a small hosted model asked named questions about a state, answering with
// probabilities and never with text. The Domain asks through this and knows nothing of who
// answers or how; the one client that does lives in Infrastructure.
//
// It never throws for the service's sake. A judge that is slow, down, rate-limited or
// misconfigured answers an absence with its reason, because every use of a judgment here is a
// head start on work the model would do anyway, and a third party must not fail a turn.
public interface IJudge
{
    Task<JudgmentOutcome> JudgeAsync(JudgmentRequest request, CancellationToken deadline);
}

public abstract record JudgmentOutcome
{
    private JudgmentOutcome()
    {
    }

    public sealed record Answered(Judgment Judgment) : JudgmentOutcome;

    public sealed record Absent(AbsenceReason Reason) : JudgmentOutcome;
}

public enum AbsenceReason
{
    // No key: the feature is off, not failing. Nothing downstream checks the key itself.
    Unconfigured,

    // The caller's deadline passed, or the client's own timeout did, before an answer came.
    Deadline,

    // The service answered something other than a judgment, or could not be reached.
    Error
}