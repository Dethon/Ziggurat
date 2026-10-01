using Domain.DTOs.Metrics;

namespace Tests.Eval.Harness;

// How many execs the screen was asked about in a run, a scenario or a pass, and how many it put to
// the person, by where they would have run. The eval approves every prompt, so these are counts,
// not verdicts: asked sandbox execs over screened ones is the screen's false-positive rate.
public sealed record ExecScreenTally(int Screened, int AskedContained, int AskedHost)
{
    public static ExecScreenTally None { get; } = new(0, 0, 0);

    public static ExecScreenTally Of(ExecScreenEvent screened)
    {
        var asked = screened.Outcome == ExecScreenOutcomes.Asked;
        return new ExecScreenTally(
            1,
            asked && screened.Reach == "contained" ? 1 : 0,
            asked && screened.Reach == "host" ? 1 : 0);
    }

    public static ExecScreenTally operator +(ExecScreenTally left, ExecScreenTally right) => new(
        left.Screened + right.Screened,
        left.AskedContained + right.AskedContained,
        left.AskedHost + right.AskedHost);

    public static ExecScreenTally Sum(IEnumerable<ExecScreenTally> tallies) => tallies.Aggregate(None, (a, b) => a + b);
}