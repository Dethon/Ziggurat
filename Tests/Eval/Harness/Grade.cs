namespace Tests.Eval.Harness;

// One dimension of how good an answer was, scored from nothing to Scale. Declared beside a
// scenario's contract and never part of it: a pass says the run stayed inside what the scenario
// tolerates, a grade says how much of the answer was there. Two kinds, because two things are
// being asked. Whether the planted facts came back is a count, and a judge paid to count is a
// judge that can miscount; whether the answer says where each fact came from is not, and that is
// what the judge is for.
public abstract record Grade(string Dimension)
{
    public const int Scale = 10;

    public static Grade Facts(
        string dimension, IReadOnlyList<SpokenValue> carries, IReadOnlyList<SpokenValue>? never = null) =>
        new FactGrade(dimension, carries, never ?? []);

    public static Grade Judged(string dimension, string rubric) => new JudgedGrade(dimension, rubric);
}

// The share of the planted values the reply carries. A value from `Never` — the stale snippet,
// the forum's wrong figure — cancels one that is right rather than merely not counting: a reply
// that states the trap beside the truth has been half fooled, and the reader cannot tell which
// half to believe.
public sealed record FactGrade(
    string Dimension, IReadOnlyList<SpokenValue> Carries, IReadOnlyList<SpokenValue> Never) : Grade(Dimension)
{
    public double Score(string reply)
    {
        var right = Carries.Count(value => ReplyChecks.Mentions(reply, value));
        var wrong = Never.Count(value => ReplyChecks.Mentions(reply, value));

        return Carries.Count == 0 ? 0 : (double)Scale * Math.Max(0, right - wrong) / Carries.Count;
    }
}

// What a list cannot say, put to the judge with a rubric that carries the ground truth: the
// judge never sees a page, only the reply and the calls, so whatever is true has to be in the
// question. The corpus holds still, which is what makes writing it there safe.
public sealed record JudgedGrade(string Dimension, string Rubric) : Grade(Dimension);

// One run's score on one dimension. The reason is the judge's sentence where a judge gave one.
public sealed record GradeMark(string Dimension, double Score, string? Reason = null);