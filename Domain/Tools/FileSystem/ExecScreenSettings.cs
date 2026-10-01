using JetBrains.Annotations;

namespace Domain.Tools.FileSystem;

// The exec screen's tunables, in the agent's own appsettings alone: no other host screens. The
// bars and the question wording were measured together (.scratch/exec-screen/probe/README.md), so
// moving one is a config edit with a probe run behind it.
public sealed record ExecScreenSettings
{
    // False skips the screen entirely: nothing is sent and nothing is asked, which is how exec ran
    // before the screen existed.
    public bool Enabled { get; [UsedImplicitly] init; } = true;

    // From the moment the judge is asked. More generous than the voice approval's, which a person
    // waits on in silence: an exec takes longer than this anyway.
    public int DeadlineMs { get; [UsedImplicitly] init; } = 1500;

    // How many of the person's latest messages are the request, and how much of each.
    public int RecentRequests { get; [UsedImplicitly] init; } = 3;

    public int RequestChars { get; [UsedImplicitly] init; } = 1000;

    // A command is asked about when it serves the request with less than this probability.
    public double ServesBar { get; [UsedImplicitly] init; } = 0.65;

    // On the person's own machine, also when it destroys or sends out with at least these.
    public double DestroysBar { get; [UsedImplicitly] init; } = 0.75;

    public double SendsOutBar { get; [UsedImplicitly] init; } = 0.6;
}