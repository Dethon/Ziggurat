using JetBrains.Annotations;

namespace Domain.Skills;

// The preload's tunables, in the agent's own appsettings alone: no other host has to agree, and
// each is a config edit with a probe run and an eval pass behind it. The three bars sit at 0.9
// because the probe found no wrong preload there in either shape, and the cap at two because a
// request has been seen to need two skills and never three.
public sealed record SkillPreloadSettings
{
    public bool Enabled { get; [UsedImplicitly] init; } = true;

    // From the moment the judge is asked; a miss is no preload and costs the turn nothing more.
    // A warm judgment from prod has measured 270–590 ms, and this sits just above that tail.
    public int DeadlineMs { get; [UsedImplicitly] init; } = 700;

    // The choice winner is preloaded at this confidence.
    public double ChoiceConfidence { get; [UsedImplicitly] init; } = 0.9;

    // Any other skill rides along at this probability on its own yes/no.
    public double NoulProbability { get; [UsedImplicitly] init; } = 0.9;

    // A `none` this confident preloads nothing, whatever the nouls say.
    public double NoneVeto { get; [UsedImplicitly] init; } = 0.9;

    public int MaxSkills { get; [UsedImplicitly] init; } = 2;

    // The reads a preloaded skill declares, all of them together. Separate from DeadlineMs and
    // more generous, because a read is a mount rendering what it holds rather than a round trip
    // to a service — but bounded all the same: the turn's first model call waits on the preload,
    // so a mount that hangs would hold the turn with no first token and nothing spoken, which is
    // the very thing the deadline exists to prevent. A read that misses it is a preload without
    // it, and the body still tells the model to read.
    public int ReadsBudgetMs { get; [UsedImplicitly] init; } = 2000;
}